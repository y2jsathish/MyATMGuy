using ATM.Application.Correlation;
using ATM.Application.Masking;
using ATM.Application.Terminal;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Parsing;

namespace ATM.Infrastructure.Logging;

public sealed class LoggingOptions
{
    public const string SectionName = "Logging";

    public string MinimumLevel { get; set; } = "Information";

    /// <summary>Per namespace minimum levels, e.g. { "ATM.Infrastructure": "Debug" }.</summary>
    public Dictionary<string, string> Overrides { get; set; } = [];

    public string Directory { get; set; } = Path.Combine(AppContext.BaseDirectory, "logs");

    public string FileName { get; set; } = "atm-.clef";

    public long FileSizeLimitBytes { get; set; } = 50L * 1024 * 1024;

    public int RetainedFileCount { get; set; } = 30;

    public bool WindowsEventLog { get; set; }

    public string EventLogSource { get; set; } = "ATMPlatform";
}

/// <summary>Holds the level switches so levels can be changed while the terminal runs.</summary>
public sealed class LogLevelController
{
    private readonly LoggingLevelSwitch root = new(LogEventLevel.Information);
    private readonly Dictionary<string, LoggingLevelSwitch> overrides = new(StringComparer.Ordinal);

    public LoggingLevelSwitch Root => root;

    public IReadOnlyDictionary<string, LoggingLevelSwitch> Overrides => overrides;

    public void Apply(LoggingOptions options)
    {
        root.MinimumLevel = Parse(options.MinimumLevel);
        foreach (var (ns, level) in options.Overrides)
        {
            if (!overrides.TryGetValue(ns, out var sw))
            {
                overrides[ns] = sw = new LoggingLevelSwitch();
            }

            sw.MinimumLevel = Parse(level);
        }
    }

    public static LogEventLevel Parse(string text) =>
        Enum.TryParse<LogEventLevel>(text, ignoreCase: true, out var level) ? level : LogEventLevel.Information;
}

/// <summary>Adds terminal, version, machine and the ambient correlation ID to every event.</summary>
public sealed class AtmEnricher(TerminalInfo terminal) : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("Terminal", terminal.TerminalId));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("Version", terminal.Version));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("Machine", terminal.Machine));
        if (CorrelationContext.Current is { } id)
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("CorrelationId", id.ToString()));
        }
    }
}

/// <summary>Masks every event on its way to the sinks: template text, string values at any depth, and exceptions.</summary>
public sealed class MaskingSink(ILogEventSink inner) : ILogEventSink, IDisposable
{
    public void Dispose() => (inner as IDisposable)?.Dispose();

    private static readonly MessageTemplateParser Parser = new();

    public void Emit(LogEvent logEvent)
    {
        var properties = logEvent.Properties
            .Select(p => new LogEventProperty(p.Key, MaskSensitiveName(p.Key, p.Value)))
            .ToList();

        if (logEvent.Exception is not null)
        {
            properties.Add(new LogEventProperty("Exception", new ScalarValue(SensitiveDataMasker.Mask(logEvent.Exception.ToString()))));
        }

        var template = Parser.Parse(SensitiveDataMasker.Mask(logEvent.MessageTemplate.Text));
        inner.Emit(new LogEvent(logEvent.Timestamp, logEvent.Level, null, template, properties, logEvent.TraceId ?? default, logEvent.SpanId ?? default));
    }

    internal static LogEventPropertyValue MaskValue(LogEventPropertyValue value) => value switch
    {
        ScalarValue { Value: string s } => new ScalarValue(SensitiveDataMasker.Mask(s)),
        SequenceValue seq => new SequenceValue(seq.Elements.Select(MaskValue)),
        StructureValue st => new StructureValue(st.Properties.Select(p => new LogEventProperty(p.Name, MaskSensitiveName(p.Name, p.Value))), st.TypeTag),
        DictionaryValue d => new DictionaryValue(d.Elements.Select(kv => new KeyValuePair<ScalarValue, LogEventPropertyValue>(kv.Key, MaskSensitiveName(kv.Key.Value?.ToString() ?? string.Empty, kv.Value)))),
        _ => value,
    };

    private static LogEventPropertyValue MaskSensitiveName(string name, LogEventPropertyValue value) =>
        value is ScalarValue && SensitiveDataMasker.IsSensitiveName(name)
            ? new ScalarValue(SensitiveDataMasker.Redacted)
            : MaskValue(value);
}

public static class AtmLogging
{
    /// <summary>Builds the production logger. Masking is always applied; there is no option to bypass it.</summary>
    public static Serilog.Core.Logger Create(LoggingOptions options, TerminalInfo terminal, LogLevelController levels, Action<LoggerSinkConfiguration>? extraSinks = null)
    {
        levels.Apply(options);

        var config = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levels.Root)
            .Enrich.With(new AtmEnricher(terminal));

        foreach (var (ns, sw) in levels.Overrides)
        {
            config.MinimumLevel.Override(ns, sw);
        }

        config.WriteTo.Sink(new MaskingSink(BuildFileSink(options)));

        if (extraSinks is not null)
        {
            config.WriteTo.Logger(lc => extraSinks(lc.WriteTo));
        }

        return config.CreateLogger();
    }

    private static ILogEventSink BuildFileSink(LoggingOptions options)
    {
        var path = Path.Combine(options.Directory, options.FileName);
        var logger = new LoggerConfiguration()
            .WriteTo.Async(a => a.File(
                new RenderedCompactJsonFormatter(),
                path,
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: options.FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: options.RetainedFileCount,
                shared: true))
            .CreateLogger();
        return new DisposingSink(logger);
    }

    private sealed class DisposingSink(Serilog.Core.Logger logger) : ILogEventSink, IDisposable
    {
        public void Emit(LogEvent logEvent) => logger.Write(logEvent);

        public void Dispose() => logger.Dispose();
    }
}
