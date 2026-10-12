using ATM.Application.Configuration;
using ATM.Application.Events;
using ATM.Application.Health;
using ATM.Application.Jobs;
using ATM.Application.Modules;
using ATM.Application.Monitoring;
using ATM.Application.Notifications;
using ATM.Application.Persistence;
using ATM.Application.Supervisor;
using ATM.Core.Errors;
using ATM.Infrastructure.Health;
using ATM.Infrastructure.Jobs;
using ATM.Infrastructure.Monitoring;
using ATM.Infrastructure.Persistence;
using ATM.Infrastructure.Resilience;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ATM.Infrastructure;

/// <summary>
/// Sprint 5 common services: event bus, health checks, notifications, scheduled jobs, resilience, central monitoring client,
/// watchdog and supervisor session. Register after configuration, infrastructure and (optionally) persistence.
/// All of these are singletons: they hold shared state (queues, caches, schedules) and no per-operation state.
/// </summary>
public sealed class PlatformServicesModule : IServiceModule
{
    public string Name => "platform";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        var monitoring = configuration.GetSection(MonitoringOptions.SectionName).Get<MonitoringOptions>() ?? new MonitoringOptions();
        var thresholds = configuration.GetSection("Health").Get<HealthThresholds>() ?? new HealthThresholds();
        var resilience = configuration.GetSection("Resilience").Get<ResilienceOptions>() ?? new ResilienceOptions();
        var supervisor = configuration.GetSection(SupervisorOptions.SectionName).Get<SupervisorOptions>() ?? new SupervisorOptions();
        var notifications = configuration.GetSection("Notifications").Get<NotificationOptions>() ?? new NotificationOptions();
        var persistence = services.Any(d => d.ServiceType == typeof(DatabaseMaintenance));
        var governed = services.Any(d => d.ServiceType == typeof(ConfigGovernor));

        services.TryAddSingleton(new AtmEnvironmentHolder(AtmEnvironments.FromEnvironment()));
        services.TryAddSingleton(monitoring);
        services.TryAddSingleton(thresholds);
        services.TryAddSingleton(resilience);
        services.TryAddSingleton(supervisor);
        services.TryAddSingleton(notifications);
        services.TryAddSingleton<ISupervisorSession, SupervisorSession>();
        services.TryAddSingleton(new RetentionOptions());

        // Event bus and dead letters
        services.TryAddSingleton<IDeadLetterStore>(sp =>
            sp.GetService<IDbContextFactory<AtmDbContext>>() is { } contexts ? new EfDeadLetterStore(contexts) : new InMemoryDeadLetterStore());
        services.TryAddSingleton<InProcessEventBus>();
        services.TryAddSingleton<IEventBus>(sp => sp.GetRequiredService<InProcessEventBus>());
        services.TryAddSingleton<DeadLetterReplayer>();
        services.AddHostedService<EventWiringHostedService>();

        // Notifications
        services.TryAddSingleton<UiBannerChannel>();
        services.AddSingleton<INotificationChannel>(sp => sp.GetRequiredService<UiBannerChannel>());
        services.AddSingleton<INotificationChannel, LogNotificationChannel>();
        services.TryAddSingleton<INotificationService, NotificationService>();

        // Resilience and central monitoring
        services.TryAddSingleton<ResiliencePipelines>();
        if (monitoring.Enabled)
        {
            services.TryAddSingleton(new Outbox(monitoring.OutboxPath, monitoring.MaxQueued));
            services.AddHttpClient<IMonitoringClient, MonitoringClient>(client =>
            {
                client.BaseAddress = new Uri(monitoring.BaseUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(30);
                if (!string.IsNullOrEmpty(monitoring.ApiKey))
                {
                    client.DefaultRequestHeaders.Add("X-Api-Key", monitoring.ApiKey);
                }
            });
            services.AddSingleton<INotificationChannel, MonitoringNotificationChannel>();
            services.AddSingleton<IScheduledJob, HeartbeatJob>();
            services.AddSingleton<IScheduledJob, OutboxReplayJob>();
        }
        else
        {
            services.TryAddSingleton<IMonitoringClient, NullMonitoringClient>();
        }

        // Health checks
        var dataRoot = Path.GetDirectoryName(Path.GetFullPath(new DatabaseOptions().Path))!;
        services.AddSingleton<IHealthCheck>(sp => new DiskSpaceCheck(sp.GetService<DatabaseOptions>()?.Path ?? dataRoot, sp.GetRequiredService<HealthThresholds>()));
        services.AddSingleton<IHealthCheck>(sp => new MemoryCheck(sp.GetRequiredService<HealthThresholds>()));
        services.AddSingleton<IHealthCheck, EventBusBacklogCheck>();
        services.AddSingleton<IHealthCheck, DeviceHealthCheck>();
        if (persistence)
        {
            services.AddSingleton<IHealthCheck, DatabaseHealthCheck>();
            services.AddSingleton<IScheduledJob, BackupJob>();
            services.AddSingleton<IScheduledJob, PurgeJob>();
        }

        if (governed)
        {
            services.AddSingleton<IHealthCheck>(sp => new ConfigurationHealthCheck(sp.GetRequiredService<ConfigGovernor>(), sp.GetRequiredService<AtmEnvironmentHolder>().Value));
        }

        services.TryAddSingleton<HealthMonitor>();
        services.AddSingleton<IScheduledJob>(sp => new HealthJob(sp.GetRequiredService<HealthMonitor>(), monitoring.Enabled ? sp.GetRequiredService<IMonitoringClient>() : null, sp.GetRequiredService<ATM.Application.Terminal.TerminalInfo>()));

        // Jobs and watchdog
        services.TryAddSingleton<JobRunner>();
        services.AddHostedService<JobHostedService>();
        services.TryAddSingleton<IClientRestarter, ProcessClientRestarter>();
        services.TryAddSingleton(sp => new Watchdog(sp.GetRequiredService<IClientRestarter>(), sp.GetRequiredService<TimeProvider>(), TimeSpan.FromSeconds(60)));
        services.AddHostedService<WatchdogHostedService>();
    }
}

/// <summary>Struct values cannot be registered as services directly, so the environment is wrapped.</summary>
public sealed record AtmEnvironmentHolder(AtmEnvironment Value);

public sealed class NullMonitoringClient : IMonitoringClient
{
    public int QueuedCount => 0;

    public Task<bool> SendHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<bool> SendHealthAsync(HealthRequest request, CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<bool> SendErrorAsync(ErrorRequest request, CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<int> ReplayQueuedAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
}

/// <summary>Turns bus events into operator notifications: unhealthy checks and opened circuit breakers.</summary>
public sealed class EventWiringHostedService(IEventBus bus, INotificationService notifications) : IHostedService
{
    private readonly List<IDisposable> subscriptions = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        subscriptions.Add(bus.Subscribe<HealthStateChanged>(
            (e, ct) =>
            {
                var severity = e.Current switch
                {
                    nameof(HealthState.Unhealthy) => ErrorSeverity.High,
                    nameof(HealthState.Degraded) => ErrorSeverity.Medium,
                    _ => ErrorSeverity.Low,
                };
                return notifications.NotifyAsync(severity, $"Health: {e.Check} is {e.Current}", e.Message, ct);
            },
            "health-notifications"));
        subscriptions.Add(bus.Subscribe<CircuitBreakerStateChanged>(
            (e, ct) => notifications.NotifyAsync(e.State == "Open" ? ErrorSeverity.High : ErrorSeverity.Low, $"Circuit {e.Pipeline} is {e.State}", "Remote calls are being short-circuited.", ct),
            "circuit-notifications"));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        subscriptions.ForEach(s => s.Dispose());
        subscriptions.Clear();
        return Task.CompletedTask;
    }
}
