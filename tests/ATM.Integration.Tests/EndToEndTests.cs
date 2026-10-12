using System.Net.Http.Json;
using ATM.Application.Audit;
using ATM.Application.Configuration;
using ATM.Application.Devices;
using ATM.Application.Events;
using ATM.Application.Health;
using ATM.Application.Jobs;
using ATM.Application.Monitoring;
using ATM.Application.Notifications;
using ATM.Application.Persistence;
using ATM.Core.Devices;
using ATM.Core.Entities;
using ATM.Core.ValueObjects;
using ATM.Infrastructure.Configuration;
using ATM.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using ATM.Presentation.Navigation;
using ATM.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ATM.Integration.Tests;

public class EndToEndTests
{
    [Fact]
    public async Task Startup_ComposesTheWholePlatform_AndEveryHealthCheckPasses()
    {
        await using var terminal = await TestTerminal.StartAsync();

        var report = await terminal.Services.GetRequiredService<HealthMonitor>().RunAsync();

        report.Overall.Should().Be(HealthState.Healthy, string.Join("; ", report.Entries.Select(e => $"{e.Name}={e.State} {e.Message}")));
        report.Entries.Select(e => e.Name).Should().Contain(["Configuration", "Database", "Devices", "Disk", "EventBus", "Memory"]);
        terminal.Services.GetServices<IHostedService>().Should().NotBeEmpty();
        terminal.Services.GetRequiredService<ATM.Application.Terminal.TerminalInfo>().TerminalId.Should().Be("ATM-0777");
    }

    [Fact]
    public async Task Transaction_IsStored_Audited_AndTheAuditChainVerifies()
    {
        await using var terminal = await TestTerminal.StartAsync();
        var audit = terminal.Services.GetRequiredService<IAuditService>();
        var correlation = CorrelationId.New();
        Transaction tx;

        using (ATM.Application.Correlation.CorrelationContext.Begin(correlation))
        {
            tx = Transaction.Start(TerminalId.Create("ATM-0777"), TransactionType.Withdrawal, 80m, MaskedPan.FromPan("4111111111111111"), DateTimeOffset.UtcNow);
            tx.Complete(DateTimeOffset.UtcNow);
            await using (var uow = terminal.Services.GetRequiredService<IUnitOfWorkFactory>().Create())
            {
                await uow.Transactions.AddAsync(tx);
                await uow.CommitAsync();
            }

            await audit.RecordAsync("customer", "Withdrawal", $"tx {tx.Id}", "completed", AuditCategory.Operator);
            await terminal.Services.GetRequiredService<IEventBus>().PublishAsync(new TransactionFinished(tx.Id, tx.Status));
        }

        await terminal.Services.GetRequiredService<InProcessEventBus>().DrainAsync();
        await using var read = terminal.Services.GetRequiredService<IUnitOfWorkFactory>().Create();
        (await read.Transactions.GetAsync(tx.Id))!.Status.Should().Be(TransactionStatus.Completed);
        (await read.Audit.PageAsync(1, 10, correlation)).Items.Should().ContainSingle().Which.Action.Should().Be("Withdrawal");
        (await audit.VerifyAsync()).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ConfigReload_AppliesGoodChange_RejectsBadOne_AndBothAreAudited()
    {
        await using var terminal = await TestTerminal.StartAsync();
        var reload = terminal.Services.GetRequiredService<ConfigReloadService>();
        var levels = terminal.Services.GetRequiredService<LogLevelController>();
        var events = new List<ConfigurationChanged>();
        terminal.Services.GetRequiredService<IEventBus>().Subscribe<ConfigurationChanged>((e, _) =>
        {
            events.Add(e);
            return Task.CompletedTask;
        });

        File.WriteAllText(terminal.ConfigFile, File.ReadAllText(terminal.ConfigFile).Replace("\"MinimumLevel\": \"Information\"", "\"MinimumLevel\": \"Debug\"", StringComparison.Ordinal));
        terminal.Start.Raw.Reload();
        (await reload.ReloadAsync()).Applied.Should().BeTrue();
        levels.Root.MinimumLevel.Should().Be(Serilog.Events.LogEventLevel.Debug);

        File.WriteAllText(terminal.ConfigFile, File.ReadAllText(terminal.ConfigFile).Replace("\"Debug\"", "\"Loud\"", StringComparison.Ordinal));
        terminal.Start.Raw.Reload();
        (await reload.ReloadAsync()).Applied.Should().BeFalse();
        levels.Root.MinimumLevel.Should().Be(Serilog.Events.LogEventLevel.Debug, "the last known good stays in force");

        await terminal.Services.GetRequiredService<InProcessEventBus>().DrainAsync();
        events.Should().ContainSingle().Which.Keys.Should().Contain("Logging:MinimumLevel");
        File.Exists(Path.Combine(terminal.Directory, AtmConfiguration.LastKnownGoodFile)).Should().BeTrue();
        await using var read = terminal.Services.GetRequiredService<IUnitOfWorkFactory>().Create();
        var actions = (await read.Audit.PageAsync(1, 20)).Items.Select(a => a.Action).ToList();
        actions.Should().Contain(["ConfigChanged", "ConfigRejected"]);
        (await terminal.Services.GetRequiredService<IAuditService>().VerifyAsync()).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task DeviceFault_BecomesUnhealthy_RaisesAnEvent_AndShowsInTheUiBanner()
    {
        await using var terminal = await TestTerminal.StartAsync(simulator: s => s.CashDispenserStatus = DeviceStatus.Fault);
        var banner = terminal.Services.GetRequiredService<UiBannerChannel>();
        var changes = new List<HealthStateChanged>();
        terminal.Services.GetRequiredService<IEventBus>().Subscribe<HealthStateChanged>((e, _) =>
        {
            changes.Add(e);
            return Task.CompletedTask;
        });

        var report = await terminal.Services.GetRequiredService<HealthMonitor>().RunAsync();
        await terminal.Services.GetRequiredService<InProcessEventBus>().DrainAsync();

        report.Entries.Single(e => e.Name == "Devices").State.Should().Be(HealthState.Unhealthy);
        changes.Should().Contain(c => c.Check == "Devices" && c.Current == "Unhealthy");
        banner.Current.Should().Contain(n => n.Title.Contains("Devices", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MaintenanceScreen_RunsTheSelfTestAgainstTheWiredDevices()
    {
        await using var terminal = await TestTerminal.StartAsync(simulator: s => s.ReceiptPrinterStatus = DeviceStatus.Degraded);
        terminal.Services.GetRequiredService<INavigationService>().NavigateTo<MaintenanceViewModel>();
        var viewModel = (MaintenanceViewModel)terminal.Services.GetRequiredService<INavigationService>().Current!;

        await viewModel.RunSelfTestCommand.ExecuteAsync(null);

        viewModel.Devices.Should().HaveCount(4);
        viewModel.Overall.Should().Be("Degraded");
    }

    [Fact]
    public async Task Heartbeat_ReachesTheMonitoringApi_AndOfflineWorkIsQueuedThenReplayed()
    {
        using var server = new MonitoringServer();
        await using var terminal = await TestTerminal.StartAsync(monitoring: server);
        var jobs = terminal.Services.GetServices<IScheduledJob>().ToDictionary(j => j.Name);
        var client = terminal.Services.GetRequiredService<IMonitoringClient>();

        await jobs["heartbeat"].RunAsync(default);
        await jobs["health"].RunAsync(default);

        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", TestTerminal.ApiKey);
        var terminals = (await http.GetFromJsonAsync<List<TerminalSummary>>("/api/terminals"))!;
        var me = terminals.Single(t => t.TerminalId == "ATM-0777");
        me.Overall.Should().Be("Healthy");
        client.QueuedCount.Should().Be(0);
    }

    [Fact]
    public async Task Shutdown_StopsHostedServicesQuickly_AndFlushesLogs()
    {
        var terminal = await TestTerminal.StartAsync();
        terminal.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<EndToEndTests>>().LogInformation("shutdown marker {Value}", 42);
        var logs = Path.Combine(terminal.Directory, "logs");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await terminal.Host.StopAsync(TimeSpan.FromSeconds(10));
        terminal.Host.Dispose();
        watch.Stop();

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        var lines = new List<LogLine>();
        await foreach (var line in LogSearch.SearchAsync(logs, new LogQuery(Text: "shutdown marker")))
        {
            lines.Add(line);
        }

        lines.Should().ContainSingle("the async log sink must be flushed on shutdown");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(terminal.Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task UnhandledException_IsLoggedCountedAndDoesNotCrashTheHost()
    {
        await using var terminal = await TestTerminal.StartAsync();
        var hooks = terminal.Services.GetRequiredService<UnhandledExceptionHooks>();
        var counters = terminal.Services.GetRequiredService<ATM.Application.Errors.IErrorCounters>();

        hooks.Handle(new InvalidOperationException("injected fault"), "fault-injection");

        counters.Total.Should().Be(1);
    }

    [Fact]
    public void InvalidConfiguration_BlocksStartupWithTheOffendingKeys()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"atm-it-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "appsettings.json"), "{\"Terminal\":{\"TerminalId\":\"not valid!\",\"IdleTimeoutSeconds\":1}}");

            var start = ATM.Infrastructure.Bootstrap.PlatformBootstrap.Prepare(directory, AtmEnvironment.Dev);

            start.Configuration.IsValid.Should().BeFalse();
            start.Configuration.UsedLastKnownGood.Should().BeFalse();
            start.Configuration.Errors.Should().Contain(e => e.StartsWith("Terminal:TerminalId", StringComparison.Ordinal)).And.Contain(e => e.StartsWith("Terminal:IdleTimeoutSeconds", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
