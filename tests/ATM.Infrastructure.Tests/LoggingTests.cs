using ATM.Application.Correlation;
using ATM.Application.Errors;
using ATM.Application.Terminal;
using ATM.Core.Errors;
using ATM.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

namespace ATM.Infrastructure.Tests;

public sealed class LoggingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"atmlogs-{Guid.NewGuid():N}");
    private readonly TerminalInfo terminal = new("ATM-0042", "1.2.3", "lab-pc");

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private Serilog.Core.Logger Create(Action<LoggingOptions>? configure = null, LogLevelController? levels = null)
    {
        var options = new LoggingOptions { Directory = directory };
        configure?.Invoke(options);
        return AtmLogging.Create(options, terminal, levels ?? new LogLevelController());
    }

    private async Task<List<LogLine>> ReadAll(LogQuery? query = null)
    {
        var lines = new List<LogLine>();
        await foreach (var line in LogSearch.SearchAsync(directory, query ?? new LogQuery()))
        {
            lines.Add(line);
        }

        return lines;
    }

    [Fact]
    public async Task Events_CarryTerminalVersionMachineAndCorrelation()
    {
        var id = ATM.Core.ValueObjects.CorrelationId.New();
        using (var logger = Create())
        using (CorrelationContext.Begin(id))
        {
            logger.Information("Started {Module}", "Core");
        }

        var line = (await ReadAll()).Single();
        line.Terminal.Should().Be("ATM-0042");
        line.CorrelationId.Should().Be(id.ToString());
        line.Raw.Should().Contain("1.2.3").And.Contain("lab-pc");
    }

    [Fact]
    public async Task Pan_InTemplateAndProperties_NeverReachesTheFile()
    {
        using (var logger = Create())
        {
            logger.Information("Card 4111111111111111 inserted");
            logger.Information("Card {Pan} inserted pin={Pin}", "4111111111111111", "1234");
            logger.Information("Payload {@Data}", new { Cvv = "123", Note = "5500005555555559" });
            logger.Error(new InvalidOperationException("failed for 4111111111111111"), "Boom");
        }

        var text = string.Join('\n', (await ReadAll()).Select(l => l.Raw));
        text.Should().NotContain("4111111111111111").And.NotContain("5500005555555559").And.NotContain("\"1234\"");
        text.Should().Contain("411111******1111");
    }

    [Fact]
    public async Task Level_CanBeChangedAtRuntime_AndPerNamespace()
    {
        var levels = new LogLevelController();
        using (var logger = Create(o => { o.MinimumLevel = "Warning"; o.Overrides["Noisy"] = "Error"; }, levels))
        {
            logger.Information("hidden");
            logger.Warning("shown-1");
            levels.Root.MinimumLevel = Serilog.Events.LogEventLevel.Information;
            logger.Information("shown-2");
            logger.ForContext("SourceContext", "Noisy.Thing").Warning("hidden-by-override");
        }

        var messages = (await ReadAll()).Select(l => l.Message).ToList();
        messages.Should().BeEquivalentTo("shown-1", "shown-2");
    }

    [Fact]
    public async Task Search_FiltersByLevelCorrelationTextAndTime()
    {
        var a = ATM.Core.ValueObjects.CorrelationId.New();
        using (var logger = Create())
        {
            using (CorrelationContext.Begin(a))
            {
                logger.Information("alpha");
                logger.Error("beta failure");
            }

            logger.Warning("gamma");
        }

        (await ReadAll(new LogQuery(MinimumLevel: "Warning"))).Should().HaveCount(2);
        (await ReadAll(new LogQuery(CorrelationId: a.ToString()))).Should().HaveCount(2);
        (await ReadAll(new LogQuery(Text: "failure"))).Should().ContainSingle();
        (await ReadAll(new LogQuery(From: DateTimeOffset.UtcNow.AddMinutes(1)))).Should().BeEmpty();
        (await ReadAll(new LogQuery(To: DateTimeOffset.UtcNow.AddMinutes(1)))).Should().HaveCount(3);
    }

    [Fact]
    public async Task Files_RollOnSizeAndAreRetentionLimited()
    {
        using (var logger = Create(o => { o.FileSizeLimitBytes = 2000; o.RetainedFileCount = 3; }))
        {
            for (var i = 0; i < 200; i++)
            {
                logger.Information("filler line number {Index} with some padding to grow the file", i);
            }
        }

        await Task.CompletedTask;
        Directory.GetFiles(directory, "*.clef").Length.Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public void UnhandledExceptionHooks_LogAndCountWithCatalogueCode()
    {
        var counters = new ErrorCounters();
        using var logger = Create();
        using var factory = LoggerFactory.Create(b => b.AddProvider(new Serilog.Extensions.Logging.SerilogLoggerProvider(logger)));
        using var hooks = new UnhandledExceptionHooks(factory.CreateLogger<UnhandledExceptionHooks>(), counters);

        hooks.Handle(new AtmException(ErrorCatalogue.DeviceFault), "test").Should().Be(ErrorCatalogue.DeviceFault);
        hooks.Handle(new InvalidOperationException("x"), "test").Should().Be(ErrorCatalogue.Unknown);

        counters.Total.Should().Be(2);
    }

    [Theory]
    [InlineData("ATM-4001", 4001)]
    [InlineData("ATM-0001", 1)]
    [InlineData("garbage", 1)]
    public void EventLog_IdsComeFromTheErrorCode(string code, int expected) =>
        ErrorCodeEventIdProvider.Parse(code).Should().Be((ushort)expected);
}
