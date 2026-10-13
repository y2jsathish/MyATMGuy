using System.Net;
using ATM.Application;
using ATM.Application.Configuration;
using ATM.Application.Devices;
using ATM.Application.Errors;
using ATM.Application.Events;
using ATM.Application.Health;
using ATM.Application.Jobs;
using ATM.Application.Modules;
using ATM.Application.Monitoring;
using ATM.Application.Terminal;
using ATM.Core.Devices;
using ATM.Core.Entities;
using ATM.Infrastructure.Configuration;
using ATM.Infrastructure.Health;
using ATM.Infrastructure.Monitoring;
using ATM.Infrastructure.Persistence;
using ATM.Infrastructure.Resilience;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ATM.Infrastructure.Tests;

public class HealthCheckTests
{
    private static readonly HealthThresholds Thresholds = new();

    [Theory]
    [InlineData(10_000_000_000, HealthState.Healthy)]
    [InlineData(1_000_000_000, HealthState.Degraded)]
    [InlineData(100_000_000, HealthState.Unhealthy)]
    public async Task Disk_ReflectsFreeSpace(long free, HealthState expected) =>
        (await new DiskSpaceCheck("/data", Thresholds, _ => free).CheckAsync()).State.Should().Be(expected);

    [Theory]
    [InlineData(100L * 1024 * 1024, HealthState.Healthy)]
    [InlineData(700L * 1024 * 1024, HealthState.Degraded)]
    [InlineData(2000L * 1024 * 1024, HealthState.Unhealthy)]
    public async Task Memory_ReflectsWorkingSet(long used, HealthState expected) =>
        (await new MemoryCheck(Thresholds, () => used).CheckAsync()).State.Should().Be(expected);

    [Fact]
    public async Task EventBusBacklog_ReflectsQueueDepth()
    {
        var bus = NSubstitute.Substitute.For<IEventBus>();
        bus.Backlog.Returns(600);
        (await new EventBusBacklogCheck(bus, Thresholds).CheckAsync()).State.Should().Be(HealthState.Degraded);
        bus.Backlog.Returns(6000);
        (await new EventBusBacklogCheck(bus, Thresholds).CheckAsync()).State.Should().Be(HealthState.Unhealthy);
    }

    [Fact]
    public async Task Devices_MapSimulatorFaultToUnhealthy()
    {
        await using var provider = new ServiceCollection()
            .AddSingleton(NSubstitute.Substitute.For<ATM.Application.Audit.IAuditStore>())
            .AddSingleton(new ATM.Application.Audit.AuditSigningKey(new byte[32]))
            .AddApplication()
            .AddInfrastructure(sim => sim.CashDispenserStatus = DeviceStatus.Fault)
            .BuildServiceProvider();

        var result = await new DeviceHealthCheck(provider.GetRequiredService<IDeviceSelfTestService>()).CheckAsync();

        result.State.Should().Be(HealthState.Unhealthy);
        result.Message.Should().Contain("CashDispenser=Fault");
    }

    [Fact]
    public async Task Configuration_IsUnhealthyWhenGovernedConfigIsInvalid()
    {
        var governor = new ConfigGovernor(_ => [], NSubstitute.Substitute.For<ATM.Application.Audit.IAuditService>(), new ErrorCounters());
        governor.Initialize(new Dictionary<string, string?> { ["Terminal:TerminalId"] = "bad id" });

        (await new ConfigurationHealthCheck(governor, AtmEnvironment.Dev).CheckAsync()).State.Should().Be(HealthState.Unhealthy);
    }
}

public class ResilienceTests
{
    private static ResiliencePipelines Create(out InProcessEventBus bus, ResilienceOptions? options = null)
    {
        bus = new InProcessEventBus(new InMemoryDeadLetterStore(), new ErrorCounters(), TimeProvider.System);
        return new ResiliencePipelines(bus, NullLogger<ResiliencePipelines>.Instance, options ?? new ResilienceOptions
        {
            RetryAttempts = 2,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            MinimumThroughput = 3,
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(5),
            BreakDuration = TimeSpan.FromMilliseconds(500),
            Timeout = TimeSpan.FromSeconds(2),
        });
    }

    [Fact]
    public async Task Transient_Failures_AreRetried()
    {
        var pipelines = Create(out var bus);
        var calls = 0;

        await pipelines.Get("t").ExecuteAsync(async _ =>
        {
            if (++calls < 3)
            {
                throw new HttpRequestException("flaky");
            }

            await Task.CompletedTask;
        });

        calls.Should().Be(3);
        await bus.DisposeAsync();
    }

    [Fact]
    public async Task Breaker_Opens_AfterRepeatedFailures_AndPublishesEvents()
    {
        var pipelines = Create(out var bus);
        var states = new List<string>();
        bus.Subscribe<CircuitBreakerStateChanged>((e, _) =>
        {
            lock (states)
            {
                states.Add(e.State);
            }

            return Task.CompletedTask;
        });
        var pipeline = pipelines.Get("monitoring");

        for (var i = 0; i < 6; i++)
        {
            try
            {
                await pipeline.ExecuteAsync(_ => throw new HttpRequestException("down"));
            }
            catch (Exception ex) when (ex is HttpRequestException or Polly.CircuitBreaker.BrokenCircuitException)
            {
            }
        }

        await bus.DrainAsync();
        states.Should().Contain("Open");

        await Task.Delay(700);
        await pipeline.ExecuteAsync(async _ => await Task.CompletedTask);
        await bus.DrainAsync();
        states.Should().Contain("Closed");
        await bus.DisposeAsync();
    }

    [Fact]
    public async Task NonTransientExceptions_AreNotRetried()
    {
        var pipelines = Create(out var bus);
        var calls = 0;

        await pipelines.Get("x").Invoking(p => p.ExecuteAsync(_ =>
        {
            calls++;
            throw new InvalidOperationException("bug");
        }).AsTask()).Should().ThrowAsync<InvalidOperationException>();

        calls.Should().Be(1);
        await bus.DisposeAsync();
    }
}

public sealed class MonitoringClientTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), $"mon-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond(request));
        }
    }

    private (MonitoringClient Client, Outbox Outbox, InProcessEventBus Bus) Create(Handler handler)
    {
        var bus = new InProcessEventBus(new InMemoryDeadLetterStore(), new ErrorCounters(), TimeProvider.System);
        var pipelines = new ResiliencePipelines(bus, NullLogger<ResiliencePipelines>.Instance, new ResilienceOptions { RetryAttempts = 1, RetryBaseDelay = TimeSpan.FromMilliseconds(1), MinimumThroughput = 1000, Timeout = TimeSpan.FromSeconds(2) });
        var outbox = new Outbox(Path.Combine(dir, "outbox.jsonl"), 100);
        var client = new MonitoringClient(new HttpClient(handler) { BaseAddress = new Uri("https://monitor.test/") }, pipelines, outbox);
        return (client, outbox, bus);
    }

    private static HeartbeatRequest Heartbeat(int n) => new("ATM-0001", "1.0", "m", "Dev", DateTimeOffset.UtcNow, n, 0);

    [Fact]
    public async Task Success_SendsImmediately_AndQueuesNothing()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var (client, outbox, bus) = Create(handler);

        (await client.SendHeartbeatAsync(Heartbeat(1))).Should().BeTrue();

        handler.Paths.Should().Equal("/api/heartbeat");
        outbox.Count.Should().Be(0);
        await bus.DisposeAsync();
    }

    [Fact]
    public async Task Offline_QueuesInOrder_AndReplaysWhenBack()
    {
        var online = false;
        var handler = new Handler(_ => online ? new HttpResponseMessage(HttpStatusCode.OK) : throw new HttpRequestException("no network"));
        var (client, outbox, bus) = Create(handler);

        await client.SendHeartbeatAsync(Heartbeat(1));
        await client.SendHeartbeatAsync(Heartbeat(2));
        await client.SendErrorAsync(new ErrorRequest("ATM-0001", "ATM-4001", "High", "x", null, DateTimeOffset.UtcNow));
        outbox.Count.Should().Be(3);

        online = true;
        handler.Paths.Clear();
        (await client.ReplayQueuedAsync()).Should().Be(3);

        handler.Paths.Should().Equal("/api/heartbeat", "/api/heartbeat", "/api/errors");
        outbox.Count.Should().Be(0);
        await bus.DisposeAsync();
    }

    [Fact]
    public async Task NewMessages_QueueBehindOlderOnes_SoOrderIsKept()
    {
        var online = false;
        var handler = new Handler(_ => online ? new HttpResponseMessage(HttpStatusCode.OK) : throw new HttpRequestException("down"));
        var (client, outbox, bus) = Create(handler);
        await client.SendHeartbeatAsync(Heartbeat(1));

        online = true;
        (await client.SendHeartbeatAsync(Heartbeat(2))).Should().BeFalse("older item must go first");

        outbox.Count.Should().Be(2);
        await bus.DisposeAsync();
    }

    [Fact]
    public async Task ServerRejection_4xx_IsDropped_NotRetriedForever()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var (client, outbox, bus) = Create(handler);

        (await client.SendHeartbeatAsync(Heartbeat(1))).Should().BeFalse();

        outbox.Count.Should().Be(0);
        await bus.DisposeAsync();
    }

    [Fact]
    public async Task ServerError_5xx_IsQueued()
    {
        var (client, outbox, bus) = Create(new Handler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        await client.SendHeartbeatAsync(Heartbeat(1));

        outbox.Count.Should().Be(1);
        await bus.DisposeAsync();
    }

    [Fact]
    public void Outbox_SurvivesRestart_AndDropsOldestPastCap()
    {
        var path = Path.Combine(dir, "ob.jsonl");
        var first = new Outbox(path, 3);
        for (var i = 0; i < 5; i++)
        {
            first.Enqueue(new OutboxItem("heartbeat", $"{{\"n\":{i}}}"));
        }

        var reopened = new Outbox(path, 3);

        reopened.Count.Should().Be(3);
        reopened.Peek()!.Payload.Should().Be("{\"n\":2}");
    }

    [Fact]
    public void Options_RequireHttps()
    {
        var flat = new Dictionary<string, string?>();
        var options = new MonitoringOptions { BaseUrl = "http://insecure" };

        System.ComponentModel.DataAnnotations.Validator.TryValidateObject(options, new System.ComponentModel.DataAnnotations.ValidationContext(options), [], validateAllProperties: true).Should().BeFalse();
        new MonitoringOptions { BaseUrl = "https://ok" }.Enabled.Should().BeTrue();
        new MonitoringOptions().Enabled.Should().BeFalse();
        flat.Should().BeEmpty();
    }
}

public sealed class PlatformModuleTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), $"plat-{Guid.NewGuid():N}");

    public PlatformModuleTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(dir, recursive: true);
    }

    private ServiceProvider Build(Dictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build();
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton(new TerminalInfo("ATM-0001", "1", "t"))
            .AddApplication()
            .AddInfrastructure(configureAudit: a =>
            {
                a.Path = Path.Combine(dir, "audit.jsonl");
                a.KeyDirectory = Path.Combine(dir, "keys");
            })
            .AddAtmPersistence(o =>
            {
                o.Path = Path.Combine(dir, "atm.db");
                o.BackupDirectory = Path.Combine(dir, "backups");
            })
            .AddModules(configuration, new PlatformServicesModule())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public async Task Container_ValidatesAndResolvesEveryPlatformService()
    {
        await using var provider = Build();

        provider.GetRequiredService<IEventBus>().Should().NotBeNull();
        provider.GetRequiredService<HealthMonitor>().Should().NotBeNull();
        provider.GetServices<IHealthCheck>().Select(c => c.Name).Should().Contain(["Disk", "Memory", "EventBus", "Devices", "Database"]);
        provider.GetServices<IScheduledJob>().Select(j => j.Name).Should().Contain(["health", "database-backup", "retention-purge"]);
        provider.GetRequiredService<IMonitoringClient>().Should().BeOfType<NullMonitoringClient>();
        provider.GetServices<IHostedService>().Should().NotBeEmpty();
    }

    [Fact]
    public async Task EnablingMonitoring_AddsHeartbeatAndOutboxJobs()
    {
        await using var provider = Build(new()
        {
            ["Monitoring:BaseUrl"] = "https://monitor.example",
            ["Monitoring:ApiKey"] = "k",
            ["Monitoring:OutboxPath"] = Path.Combine(dir, "outbox.jsonl"),
        });

        provider.GetServices<IScheduledJob>().Select(j => j.Name).Should().Contain(["heartbeat", "monitoring-outbox"]);
        provider.GetRequiredService<IMonitoringClient>().Should().BeOfType<MonitoringClient>();
    }

    [Fact]
    public void DuplicateModule_IsRejected()
    {
        var act = () => new ServiceCollection().AddModules(new ConfigurationBuilder().Build(), new PlatformServicesModule(), new PlatformServicesModule());

        act.Should().Throw<InvalidOperationException>().WithMessage("*twice*");
    }

    [Fact]
    public void CaptiveDependency_FailsContainerValidation()
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopedThing>();
        services.AddSingleton<SingletonThing>();

        var act = () => services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        act.Should().Throw<AggregateException>();
    }

    private sealed class ScopedThing;

    private sealed class SingletonThing(ScopedThing dependency)
    {
        public ScopedThing Dependency { get; } = dependency;
    }

    [Fact]
    public async Task DeadLetters_PersistInDatabase_AndReplay()
    {
        await using var provider = Build();
        await provider.GetRequiredService<DatabaseMaintenance>().InitializeAsync();
        var store = provider.GetRequiredService<IDeadLetterStore>();
        store.Should().BeOfType<EfDeadLetterStore>();

        await store.AddAsync(new DeadLetter(Guid.NewGuid(), "health.changed", EventTypes.Serialize(new HealthStateChanged("Db", "Healthy", "Unhealthy", "x")), "h", "boom pin=1234", 3, DateTimeOffset.UtcNow));

        var letters = await store.ListAsync();
        letters.Should().ContainSingle().Which.Error.Should().NotContain("1234");
        var bus = provider.GetRequiredService<IEventBus>();
        var seen = new List<HealthStateChanged>();
        bus.Subscribe<HealthStateChanged>((e, _) =>
        {
            seen.Add(e);
            return Task.CompletedTask;
        });
        (await provider.GetRequiredService<DeadLetterReplayer>().ReplayAllAsync()).Should().Be(1);
        await provider.GetRequiredService<InProcessEventBus>().DrainAsync();
        seen.Should().ContainSingle();
        (await store.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task HealthMonitor_RunsAllChecksAgainstRealServices()
    {
        await using var provider = Build();
        await provider.GetRequiredService<DatabaseMaintenance>().InitializeAsync();

        var report = await provider.GetRequiredService<HealthMonitor>().RunAsync();

        report.Entries.Select(e => e.Name).Should().Contain(["Database", "Devices", "Disk", "Memory", "EventBus"]);
        report.Entries.Single(e => e.Name == "Database").State.Should().Be(HealthState.Healthy);
        report.Entries.Single(e => e.Name == "Devices").State.Should().Be(HealthState.Healthy);
    }

    [Fact]
    public async Task Migrations_ApplyInSequence_FromEmpty()
    {
        await using var provider = Build();
        await provider.GetRequiredService<DatabaseMaintenance>().InitializeAsync();

        await using var db = await provider.GetRequiredService<IDbContextFactory<AtmDbContext>>().CreateDbContextAsync();
        (await db.Database.GetAppliedMigrationsAsync()).Should().HaveCount(2);
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }
}
