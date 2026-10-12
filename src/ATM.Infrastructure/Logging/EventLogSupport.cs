using System.Runtime.Versioning;
using Serilog;
using Serilog.Configuration;
using Serilog.Events;
using Serilog.Sinks.EventLog;

namespace ATM.Infrastructure.Logging;

/// <summary>Maps catalogue codes such as ATM-4001 to Windows Event Log event IDs (4001).</summary>
public sealed class ErrorCodeEventIdProvider : IEventIdProvider
{
    public const ushort DefaultEventId = 1;

    public ushort ComputeEventId(LogEvent logEvent)
    {
        if (logEvent.Properties.TryGetValue("ErrorCode", out var value) && value is ScalarValue { Value: string code })
        {
            return Parse(code);
        }

        return DefaultEventId;
    }

    public static ushort Parse(string code)
    {
        var dash = code.LastIndexOf('-');
        return dash >= 0 && ushort.TryParse(code.AsSpan(dash + 1), out var id) ? id : DefaultEventId;
    }
}

public static class EventLogSupport
{
    /// <summary>Adds Warning-and-above Event Log output. The source must be registered by the installer; failure here is never fatal.</summary>
    [SupportedOSPlatform("windows")]
    public static void TryAddEventLog(LoggerSinkConfiguration sinks, LoggingOptions options)
    {
        try
        {
            sinks.EventLog(
                options.EventLogSource,
                logName: "Application",
                restrictedToMinimumLevel: LogEventLevel.Warning,
                manageEventSource: false,
                eventIdProvider: new ErrorCodeEventIdProvider());
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.SecurityException or UnauthorizedAccessException)
        {
            // Source not registered or no rights: file logging still works.
        }
    }
}
