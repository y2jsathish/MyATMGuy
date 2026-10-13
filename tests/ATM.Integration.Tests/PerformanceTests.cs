using System.Diagnostics;
using ATM.Application.Events;
using ATM.Application.Persistence;
using ATM.Application.Terminal;
using ATM.Core.Entities;
using ATM.Core.ValueObjects;
using ATM.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace ATM.Integration.Tests;

/// <summary>
/// Indicative checks of NFR-01 to NFR-04 on whatever machine runs them (CI runner or developer PC). The authoritative numbers
/// come from the reference ATM PC; these thresholds are the blueprint targets, so a pass here is necessary but not sufficient.
/// </summary>
[Trait("Category", "Performance")]
public class PerformanceTests(ITestOutputHelper output)
{
    // Shared CI runners fsync far slower than an ATM PC; the workflow sets ATM_PERF_BUDGET_FACTOR to widen the disk-bound budget.
    private static double BudgetFactor =>
        double.TryParse(Environment.GetEnvironmentVariable("ATM_PERF_BUDGET_FACTOR"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) && f >= 1 ? f : 1.0;

    private static double Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[(int)Math.Ceiling(p * sorted.Length) - 1];
    }

    [Fact]
    public async Task NFR01_ColdStartToReady_IsUnderTenSeconds()
    {
        await using var terminal = await TestTerminal.StartAsync();

        output.WriteLine($"Cold start: {terminal.StartupTime.TotalMilliseconds:0} ms");
        terminal.StartupTime.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task NFR02_LogWrite_OverheadOnCallerThread_P95UnderOneMillisecond()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"perf-log-{Guid.NewGuid():N}");
        try
        {
            using var logger = AtmLogging.Create(new LoggingOptions { Directory = dir }, new TerminalInfo("ATM-0001", "1", "t"), new LogLevelController());
            for (var i = 0; i < 500; i++)
            {
                logger.Information("warm-up {Index}", i);
            }

            var samples = new List<double>();
            for (var i = 0; i < 5000; i++)
            {
                var start = Stopwatch.GetTimestamp();
                logger.Information("Card {Pan} read for {Terminal} at {Index}", "4111111111111111", "ATM-0001", i);
                samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }

            var p95 = Percentile(samples, 0.95);
            output.WriteLine($"Log write p95: {p95:0.000} ms");
            p95.Should().BeLessThan(1.0);
        }
        finally
        {
            await Task.Delay(100);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task NFR03_TransactionInsert_P95UnderTwentyMilliseconds()
    {
        await using var terminal = await TestTerminal.StartAsync();
        var factory = terminal.Services.GetRequiredService<IUnitOfWorkFactory>();
        var samples = new List<double>();

        for (var i = 0; i < 300; i++)
        {
            var tx = Transaction.Start(TerminalId.Create("ATM-0777"), TransactionType.Withdrawal, 20m, MaskedPan.FromPan("4111111111111111"), DateTimeOffset.UtcNow);
            var start = Stopwatch.GetTimestamp();
            await using (var uow = factory.Create())
            {
                await uow.Transactions.AddAsync(tx);
                await uow.CommitAsync();
            }

            samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        var p95 = Percentile(samples.Skip(20), 0.95); // skip connection warm-up
        output.WriteLine($"Transaction insert p95: {p95:0.00} ms (synchronous=FULL, WAL)");
        p95.Should().BeLessThan(20.0 * BudgetFactor);
    }

    [Fact]
    public async Task NFR04_EventBus_DispatchP95UnderFiveMilliseconds_AndSustainsAThousandPerSecond()
    {
        await using var bus = new InProcessEventBus(new InMemoryDeadLetterStore(), new ATM.Application.Errors.ErrorCounters(), TimeProvider.System);
        var latencies = new List<double>();
        bus.Subscribe<ConfigurationChanged>((e, _) =>
        {
            lock (latencies)
            {
                latencies.Add(Stopwatch.GetElapsedTime(long.Parse(e.Source, System.Globalization.CultureInfo.InvariantCulture)).TotalMilliseconds);
            }

            return Task.CompletedTask;
        });

        var total = Stopwatch.StartNew();
        for (var i = 0; i < 3000; i++)
        {
            await bus.PublishAsync(new ConfigurationChanged(["k"], Stopwatch.GetTimestamp().ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (i % 100 == 99)
            {
                await Task.Delay(100); // pace to 1,000 events per second
            }
        }

        await bus.DrainAsync();
        total.Stop();

        var p95 = Percentile(latencies, 0.95);
        output.WriteLine($"Event dispatch p95: {p95:0.000} ms; {3000 / total.Elapsed.TotalSeconds:0} events/s");
        latencies.Should().HaveCount(3000);
        p95.Should().BeLessThan(5.0);
        (3000 / total.Elapsed.TotalSeconds).Should().BeGreaterThan(900);
    }
}
