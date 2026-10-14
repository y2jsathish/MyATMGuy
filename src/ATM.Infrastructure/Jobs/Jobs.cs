using System.Diagnostics;
using ATM.Application.Configuration;
using ATM.Application.Errors;
using ATM.Application.Health;
using ATM.Application.Jobs;
using ATM.Application.Monitoring;
using ATM.Application.Notifications;
using ATM.Application.Persistence;
using ATM.Application.Terminal;
using ATM.Application.Transactions;
using ATM.Core.Errors;
using ATM.Infrastructure.Monitoring;
using ATM.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ATM.Infrastructure.Jobs;

public sealed class HealthJob(HealthMonitor monitor, IMonitoringClient? monitoring, TerminalInfo terminal) : IScheduledJob
{
    public string Name => "health";

    public JobSchedule Schedule { get; } = JobSchedule.Every(TimeSpan.FromSeconds(30));

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var report = await monitor.RunAsync(cancellationToken).ConfigureAwait(false);
        if (monitoring is not null)
        {
            await monitoring.SendHealthAsync(
                new HealthRequest(terminal.TerminalId, report.Overall.ToString(), [.. report.Entries.Select(e => new HealthEntryDto(e.Name, e.State.ToString(), e.Message, e.LastRunUtc))], report.GeneratedUtc),
                cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class HeartbeatJob(IMonitoringClient monitoring, MonitoringOptions options, TerminalInfo terminal, AtmEnvironmentHolder environment, IErrorCounters counters, TimeProvider clock) : IScheduledJob
{
    public string Name => "heartbeat";

    public JobSchedule Schedule { get; } = JobSchedule.Every(TimeSpan.FromSeconds(options.HeartbeatSeconds));

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var uptime = (long)(clock.GetUtcNow() - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds;
        await monitoring.SendHeartbeatAsync(
            new HeartbeatRequest(terminal.TerminalId, terminal.Version, terminal.Machine, environment.Value.ToString(), clock.GetUtcNow(), uptime, counters.Total),
            cancellationToken).ConfigureAwait(false);
    }
}

public sealed class OutboxReplayJob(IMonitoringClient monitoring) : IScheduledJob
{
    public string Name => "monitoring-outbox";

    public JobSchedule Schedule { get; } = JobSchedule.Every(TimeSpan.FromSeconds(30));

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (monitoring.QueuedCount > 0)
        {
            await monitoring.ReplayQueuedAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Sends reversals the host has not accepted yet. A run that finds nothing due does nothing.</summary>
public sealed class ReversalRetryJob(ReversalService reversals) : IScheduledJob
{
    public string Name => "reversal-retry";

    public JobSchedule Schedule { get; } = JobSchedule.Every(TimeSpan.FromSeconds(30));

    public async Task RunAsync(CancellationToken cancellationToken) => await reversals.RetryDueAsync(cancellationToken).ConfigureAwait(false);
}

public sealed class BackupJob(DatabaseMaintenance maintenance) : IScheduledJob
{
    public string Name => "database-backup";

    public JobSchedule Schedule { get; } = JobSchedule.DailyAtUtc(new TimeOnly(3, 0));

    public async Task RunAsync(CancellationToken cancellationToken) => await maintenance.BackupAsync(cancellationToken).ConfigureAwait(false);
}

public sealed class PurgeJob(DatabaseMaintenance maintenance, RetentionOptions retention) : IScheduledJob
{
    public string Name => "retention-purge";

    public JobSchedule Schedule { get; } = JobSchedule.DailyAtUtc(new TimeOnly(2, 0));

    public async Task RunAsync(CancellationToken cancellationToken) => await maintenance.PurgeAsync(retention, cancellationToken).ConfigureAwait(false);
}

/// <summary>Runs the scheduled jobs for the life of the host and stops them gracefully.</summary>
public sealed class JobHostedService(JobRunner runner, ILogger<JobHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Job scheduler started with {JobCount} job(s)", runner.Status().Count);
        await runner.RunLoopAsync(TimeSpan.FromSeconds(1), stoppingToken, TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        logger.LogInformation("Job scheduler stopped");
    }
}

public sealed class WatchdogHostedService(Watchdog watchdog) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                watchdog.Check();
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }
}

/// <summary>Starts a fresh copy of this process and exits the hung one.</summary>
public sealed class ProcessClientRestarter(ILogger<ProcessClientRestarter> logger) : IClientRestarter
{
    public void Restart(string reason)
    {
        logger.LogCritical("Restarting the client: {Reason}", reason);
        if (Environment.ProcessPath is { } exe)
        {
            Process.Start(new ProcessStartInfo(exe, Environment.GetCommandLineArgs().Skip(1)) { UseShellExecute = false });
        }

        Environment.Exit(ErrorExitCode);
    }

    public const int ErrorExitCode = 70;
}

public sealed class LogNotificationChannel(ILogger<LogNotificationChannel> logger) : INotificationChannel
{
    public string Name => "log";

    public Task SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        var level = notification.Severity switch
        {
            ErrorSeverity.Critical => LogLevel.Critical,
            ErrorSeverity.High => LogLevel.Error,
            ErrorSeverity.Medium => LogLevel.Warning,
            _ => LogLevel.Information,
        };
        logger.Log(level, "Notification {Title}: {Message}", notification.Title, notification.Message);
        return Task.CompletedTask;
    }
}

public sealed class MonitoringNotificationChannel(IMonitoringClient monitoring, TerminalInfo terminal) : INotificationChannel
{
    public string Name => "monitoring";

    public Task SendAsync(Notification notification, CancellationToken cancellationToken = default) =>
        monitoring.SendErrorAsync(new ErrorRequest(terminal.TerminalId, "NOTIFY", notification.Severity.ToString(), $"{notification.Title}: {notification.Message}", null, notification.CreatedUtc), cancellationToken);
}
