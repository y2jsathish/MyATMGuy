using ATM.Application.Errors;
using ATM.Application.Events;
using ATM.Application.Health;
using ATM.Application.Jobs;
using ATM.Application.Notifications;
using ATM.Application.Supervisor;
using ATM.Core.Devices;
using ATM.Core.Errors;
using Microsoft.Extensions.Time.Testing;

namespace ATM.Application.Tests;

public class EventBusTests
{
    private static (InProcessEventBus Bus, InMemoryDeadLetterStore Dead, ErrorCounters Counters) Create(int attempts = 3)
    {
        var dead = new InMemoryDeadLetterStore();
        var counters = new ErrorCounters();
        return (new InProcessEventBus(dead, counters, TimeProvider.System, new EventBusOptions { MaxAttempts = attempts, RetryDelay = TimeSpan.Zero }), dead, counters);
    }

    [Fact]
    public async Task Subscribers_ReceiveEventsInPublishOrder()
    {
        var (bus, _, _) = Create();
        var seen = new List<string>();
        bus.Subscribe<ConfigurationChanged>((e, _) =>
        {
            lock (seen)
            {
                seen.Add(e.Source);
            }

            return Task.CompletedTask;
        });

        for (var i = 0; i < 200; i++)
        {
            await bus.PublishAsync(new ConfigurationChanged(["k"], i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        await bus.DrainAsync();

        seen.Should().Equal(Enumerable.Range(0, 200).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        bus.Backlog.Should().Be(0);
    }

    [Fact]
    public async Task FailingHandler_DoesNotAffectOthers_AndEventIsDeadLettered()
    {
        var (bus, dead, counters) = Create(attempts: 2);
        var attempts = 0;
        var good = 0;
        bus.Subscribe<HealthStateChanged>((_, _) => throw new InvalidOperationException($"boom {++attempts}"), "bad");
        bus.Subscribe<HealthStateChanged>((_, _) =>
        {
            good++;
            return Task.CompletedTask;
        }, "good");

        await bus.PublishAsync(new HealthStateChanged("Disk", "Healthy", "Unhealthy", "full"));
        await bus.PublishAsync(new HealthStateChanged("Disk", "Unhealthy", "Healthy", "ok"));
        await bus.DrainAsync();

        attempts.Should().Be(4); // 2 events x 2 attempts
        good.Should().Be(2);
        var letters = await dead.ListAsync();
        letters.Should().HaveCount(2);
        letters[0].Handler.Should().Be("bad");
        letters[0].Error.Should().Contain("boom");
        letters[0].EventType.Should().Be("health.changed");
        counters.Total.Should().Be(2);
    }

    [Fact]
    public async Task TransientFailure_IsRetriedAndSucceeds()
    {
        var (bus, dead, _) = Create(attempts: 3);
        var calls = 0;
        bus.Subscribe<ConfigurationChanged>((_, _) => ++calls < 3 ? throw new InvalidOperationException("flaky") : Task.CompletedTask);

        await bus.PublishAsync(new ConfigurationChanged(["a"], "t"));
        await bus.DrainAsync();

        calls.Should().Be(3);
        (await dead.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task DeadLetters_CanBeReplayed()
    {
        var (bus, dead, _) = Create(attempts: 1);
        var fail = true;
        var received = new List<HealthStateChanged>();
        bus.Subscribe<HealthStateChanged>((e, _) =>
        {
            if (fail)
            {
                throw new InvalidOperationException("down");
            }

            received.Add(e);
            return Task.CompletedTask;
        });
        await bus.PublishAsync(new HealthStateChanged("Db", "Healthy", "Unhealthy", "x"));
        await bus.DrainAsync();
        (await dead.ListAsync()).Should().ContainSingle();

        fail = false;
        var replayed = await new DeadLetterReplayer(dead, bus).ReplayAllAsync();
        await bus.DrainAsync();

        replayed.Should().Be(1);
        received.Should().ContainSingle().Which.Check.Should().Be("Db");
        (await dead.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Unsubscribe_StopsDelivery()
    {
        var (bus, _, _) = Create();
        var count = 0;
        var subscription = bus.Subscribe<ConfigurationChanged>((_, _) =>
        {
            count++;
            return Task.CompletedTask;
        });

        await bus.PublishAsync(new ConfigurationChanged([], "a"));
        await bus.DrainAsync();
        subscription.Dispose();
        await bus.PublishAsync(new ConfigurationChanged([], "b"));
        await bus.DrainAsync();

        count.Should().Be(1);
    }

    [Fact]
    public async Task AmbientCorrelation_IsStampedOnEvents_AndRestoredInHandlers()
    {
        var (bus, _, _) = Create();
        Guid? onEvent = null;
        Guid? inHandler = null;
        bus.Subscribe<ConfigurationChanged>((e, _) =>
        {
            onEvent = e.CorrelationId;
            inHandler = Correlation.CorrelationContext.Current?.Value;
            return Task.CompletedTask;
        });

        var id = ATM.Core.ValueObjects.CorrelationId.New();
        using (Correlation.CorrelationContext.Begin(id))
        {
            await bus.PublishAsync(new ConfigurationChanged([], "x"));
        }

        await bus.DrainAsync();

        onEvent.Should().Be(id.Value);
        inHandler.Should().Be(id.Value);
    }

    [Fact]
    public async Task Publish_AfterDispose_Throws()
    {
        var (bus, _, _) = Create();
        bus.Subscribe<ConfigurationChanged>((_, _) => Task.CompletedTask);
        await bus.PublishAsync(new ConfigurationChanged([], "x"));
        await bus.DisposeAsync();

        await bus.Invoking(b => b.PublishAsync(new ConfigurationChanged([], "late")).AsTask()).Should().ThrowAsync<InvalidOperationException>();
    }
}

public class EventContractTests
{
    [Fact]
    public void EveryEvent_HasUniqueNameAndRoundTripsThroughJson()
    {
        EventTypes.Names.Should().OnlyHaveUniqueItems();
        var samples = new AtmEvent[]
        {
            new DeviceStatusChanged(DeviceKind.CashDispenser, DeviceStatus.Ok, DeviceStatus.Fault),
            new ConfigurationChanged(["A:B"], "reload"),
            new CircuitBreakerStateChanged("monitoring", "Open"),
            new HealthStateChanged("Disk", "Healthy", "Degraded", "low"),
            new TransactionFinished(Guid.NewGuid(), ATM.Core.Entities.TransactionStatus.Completed),
        };

        foreach (var sample in samples)
        {
            var name = EventTypes.NameOf(sample.GetType());
            var back = EventTypes.Deserialize(name, EventTypes.Serialize(sample));
            back.Should().BeEquivalentTo(sample, o => o.RespectingRuntimeTypes(), name);
        }
    }

    [Fact]
    public void PublishedContracts_AreFrozen()
    {
        // Guards the stored/replayed JSON shape. If this fails you changed a published contract: add a new version instead.
        EventTypes.Serialize(new DeviceStatusChanged(DeviceKind.CardReader, DeviceStatus.Ok, DeviceStatus.Offline) { EventId = Guid.Empty, OccurredUtc = DateTimeOffset.UnixEpoch })
            .Should().Be("{\"kind\":\"CardReader\",\"previous\":\"Ok\",\"current\":\"Offline\",\"version\":1,\"eventId\":\"00000000-0000-0000-0000-000000000000\",\"occurredUtc\":\"1970-01-01T00:00:00+00:00\",\"correlationId\":null}");
    }

    [Fact]
    public void EventVersions_AreAtLeastOne()
    {
        new DeviceStatusChanged(DeviceKind.CardReader, DeviceStatus.Ok, DeviceStatus.Ok).Version.Should().BeGreaterThanOrEqualTo(1);
    }
}

public class HealthMonitorTests
{
    private sealed class FakeCheck(string name, Func<HealthCheckResult> result) : IHealthCheck
    {
        public string Name => name;

        public Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(result());
    }

    [Fact]
    public async Task Overall_IsWorstState_AndChangesArePublished()
    {
        var dead = new InMemoryDeadLetterStore();
        await using var bus = new InProcessEventBus(dead, new ErrorCounters(), TimeProvider.System);
        var changes = new List<HealthStateChanged>();
        bus.Subscribe<HealthStateChanged>((e, _) =>
        {
            changes.Add(e);
            return Task.CompletedTask;
        });
        var disk = HealthCheckResult.Healthy();
        var monitor = new HealthMonitor(
            [new FakeCheck("Disk", () => disk), new FakeCheck("Db", () => HealthCheckResult.Degraded("slow")), new FakeCheck("Boom", () => throw new InvalidOperationException("x"))],
            bus,
            TimeProvider.System);

        var first = await monitor.RunAsync();
        disk = HealthCheckResult.Unhealthy("full");
        var second = await monitor.RunAsync();
        await bus.DrainAsync();

        first.Overall.Should().Be(HealthState.Unhealthy);
        first.Entries.Single(e => e.Name == "Boom").Message.Should().Contain("Check failed");
        second.Entries.Single(e => e.Name == "Disk").State.Should().Be(HealthState.Unhealthy);
        changes.Should().Contain(c => c.Check == "Disk" && c.Previous == "Healthy" && c.Current == "Unhealthy");
        changes.Should().NotContain(c => c.Check == "Disk" && c.Previous == "Unknown");
    }

    [Fact]
    public async Task NoChecks_IsHealthy()
    {
        await using var bus = new InProcessEventBus(new InMemoryDeadLetterStore(), new ErrorCounters(), TimeProvider.System);

        (await new HealthMonitor([], bus, TimeProvider.System).RunAsync()).Overall.Should().Be(HealthState.Healthy);
    }
}

public class NotificationTests
{
    private sealed class Capture(string name, bool fail = false) : INotificationChannel
    {
        public string Name => name;

        public List<Notification> Received { get; } = [];

        public Task SendAsync(Notification notification, CancellationToken cancellationToken = default)
        {
            if (fail)
            {
                throw new InvalidOperationException("channel down");
            }

            Received.Add(notification);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Routes_BySeverityPerChannel_AndMasksContent()
    {
        var log = new Capture("log");
        var ui = new Capture("ui");
        var broken = new Capture("monitoring", fail: true);
        var service = new NotificationService([log, broken, ui], new NotificationOptions(), TimeProvider.System);

        await service.NotifyAsync(ErrorSeverity.Low, "info", "card 4111111111111111");
        await service.NotifyAsync(ErrorSeverity.Critical, "boom", "cash dispenser jammed");

        log.Received.Should().HaveCount(2);
        log.Received[0].Message.Should().NotContain("4111111111111111");
        ui.Received.Should().ContainSingle().Which.Title.Should().Be("boom");
    }

    [Fact]
    public async Task UiBanner_KeepsNewestFirst_AndRaisesChanged()
    {
        var banner = new UiBannerChannel();
        var changes = 0;
        banner.Changed += (_, _) => changes++;

        await banner.SendAsync(new Notification(ErrorSeverity.High, "a", "m", DateTimeOffset.UtcNow));
        await banner.SendAsync(new Notification(ErrorSeverity.High, "b", "m", DateTimeOffset.UtcNow));

        banner.Current.Select(n => n.Title).Should().Equal("b", "a");
        banner.Clear();
        banner.Current.Should().BeEmpty();
        changes.Should().Be(3);
    }
}

public class JobRunnerTests
{
    private sealed class Job(string name, JobSchedule schedule, Func<CancellationToken, Task> body) : IScheduledJob
    {
        public string Name => name;

        public JobSchedule Schedule => schedule;

        public int Runs { get; private set; }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            Runs++;
            await body(cancellationToken);
        }
    }

    [Fact]
    public async Task IntervalJob_RunsWhenDue_NotBefore()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var job = new Job("hb", JobSchedule.Every(TimeSpan.FromSeconds(60)), _ => Task.CompletedTask);
        var runner = new JobRunner([job], clock);

        await Task.WhenAll(runner.StartDueJobs(default));
        job.Runs.Should().Be(0);

        clock.Advance(TimeSpan.FromSeconds(61));
        await Task.WhenAll(runner.StartDueJobs(default));
        await Task.WhenAll(runner.StartDueJobs(default));

        job.Runs.Should().Be(1);
        runner.Status().Single().RunCount.Should().Be(1);
    }

    [Fact]
    public async Task Job_NeverOverlapsItself()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var gate = new TaskCompletionSource();
        var job = new Job("slow", JobSchedule.Every(TimeSpan.FromSeconds(1)), _ => gate.Task);
        var runner = new JobRunner([job], clock);

        clock.Advance(TimeSpan.FromSeconds(2));
        var first = runner.StartDueJobs(default);
        clock.Advance(TimeSpan.FromSeconds(5));
        var second = runner.StartDueJobs(default);

        first.Should().HaveCount(1);
        second.Should().BeEmpty();
        job.Runs.Should().Be(1);
        runner.Status().Single().Running.Should().BeTrue();

        gate.SetResult();
        await Task.WhenAll(first);
        runner.Status().Single().Running.Should().BeFalse();
    }

    [Fact]
    public async Task FailingJob_IsReported_AndOthersStillRun()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var bad = new Job("bad", JobSchedule.Every(TimeSpan.FromSeconds(1)), _ => throw new InvalidOperationException("nope"));
        var good = new Job("good", JobSchedule.Every(TimeSpan.FromSeconds(1)), _ => Task.CompletedTask);
        var runner = new JobRunner([bad, good], clock);

        clock.Advance(TimeSpan.FromSeconds(2));
        await Task.WhenAll(runner.StartDueJobs(default));

        good.Runs.Should().Be(1);
        runner.Status().Single(s => s.Name == "bad").LastError.Should().Be("nope");
        runner.Status().Single(s => s.Name == "good").LastError.Should().BeNull();
    }

    [Fact]
    public void DailySchedule_PicksNextOccurrence()
    {
        var schedule = JobSchedule.DailyAtUtc(new TimeOnly(2, 0));

        schedule.NextAfter(DateTimeOffset.Parse("2026-01-01T01:00:00Z", System.Globalization.CultureInfo.InvariantCulture)).Should().Be(DateTimeOffset.Parse("2026-01-01T02:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        schedule.NextAfter(DateTimeOffset.Parse("2026-01-01T02:00:00Z", System.Globalization.CultureInfo.InvariantCulture)).Should().Be(DateTimeOffset.Parse("2026-01-02T02:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Shutdown_CancelsRunningJobs_WithinGracePeriod()
    {
        var job = new Job("long", JobSchedule.Every(TimeSpan.FromMilliseconds(20)), async ct => await Task.Delay(Timeout.Infinite, ct));
        var runner = new JobRunner([job], TimeProvider.System);
        using var cts = new CancellationTokenSource();

        var loop = runner.RunLoopAsync(TimeSpan.FromMilliseconds(10), cts.Token, TimeSpan.FromSeconds(2));
        await Task.Delay(150);
        cts.Cancel();

        await loop.WaitAsync(TimeSpan.FromSeconds(3));
        job.Runs.Should().Be(1);
    }
}

public class WatchdogTests
{
    private sealed class Restarter : IClientRestarter
    {
        public List<string> Reasons { get; } = [];

        public void Restart(string reason) => Reasons.Add(reason);
    }

    [Fact]
    public void SilentUi_TriggersRestartOnce_AfterTimeout()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var restarter = new Restarter();
        var watchdog = new Watchdog(restarter, clock, TimeSpan.FromSeconds(60));

        clock.Advance(TimeSpan.FromSeconds(30));
        watchdog.Check().Should().BeFalse();
        watchdog.Beat();
        clock.Advance(TimeSpan.FromSeconds(59));
        watchdog.Check().Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(2));
        watchdog.Check().Should().BeTrue();
        watchdog.Check().Should().BeFalse();

        restarter.Reasons.Should().ContainSingle().Which.Should().Contain("61");
    }
}

public class SupervisorTests
{
    private static SupervisorOptions Options() => new() { PasscodeHash = PasscodeHasher.Hash("2468") };

    [Fact]
    public void CorrectPasscode_GrantsAccess_AndExitRevokes()
    {
        var session = new SupervisorSession(Options(), TimeProvider.System);

        session.TryEnter("2468").Should().Be(SupervisorResult.Granted);
        session.IsActive.Should().BeTrue();
        session.Exit();
        session.IsActive.Should().BeFalse();
    }

    [Fact]
    public void ThreeWrongAttempts_LockOut_UntilTimeoutPasses()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var session = new SupervisorSession(Options(), clock);

        session.TryEnter("0000").Should().Be(SupervisorResult.Denied);
        session.TryEnter("0000").Should().Be(SupervisorResult.Denied);
        session.TryEnter("0000").Should().Be(SupervisorResult.LockedOut);
        session.TryEnter("2468").Should().Be(SupervisorResult.LockedOut);

        clock.Advance(TimeSpan.FromSeconds(61));
        session.TryEnter("2468").Should().Be(SupervisorResult.Granted);
    }

    [Fact]
    public void NoConfiguredHash_DisablesSupervisorMode() =>
        new SupervisorSession(new SupervisorOptions(), TimeProvider.System).TryEnter("anything").Should().Be(SupervisorResult.Disabled);

    [Fact]
    public void Hash_IsSalted_AndNeverContainsThePasscode()
    {
        var a = PasscodeHasher.Hash("2468");
        var b = PasscodeHasher.Hash("2468");

        a.Should().NotBe(b).And.NotContain("2468");
        PasscodeHasher.Verify("2468", a).Should().BeTrue();
        PasscodeHasher.Verify("2469", a).Should().BeFalse();
        PasscodeHasher.Verify("2468", "garbage").Should().BeFalse();
    }
}
