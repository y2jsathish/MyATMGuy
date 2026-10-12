namespace ATM.Application.Terminal;

/// <summary>Identity stamped on every log event and audit record.</summary>
public sealed record TerminalInfo(string TerminalId, string Version, string Machine)
{
    public static TerminalInfo FromEnvironment() => new(
        Environment.GetEnvironmentVariable("ATM_TERMINAL_ID") ?? "ATM-0001",
        typeof(TerminalInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        Environment.MachineName);
}
