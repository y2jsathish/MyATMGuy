namespace ATM.Core.Errors;

public enum ErrorSeverity
{
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>A catalogued error: a stable code, a severity and a description safe to show to operators.</summary>
public sealed record ErrorCode(string Code, ErrorSeverity Severity, string Description);

public static class ErrorCatalogue
{
    public static readonly ErrorCode Unknown = new("ATM-0001", ErrorSeverity.High, "Unclassified error.");
    public static readonly ErrorCode InvalidConfiguration = new("ATM-1012", ErrorSeverity.Medium, "Configuration validation failed.");
    public static readonly ErrorCode DeviceFault = new("ATM-4001", ErrorSeverity.High, "A device reported a fault.");
    public static readonly ErrorCode DeviceOffline = new("ATM-4002", ErrorSeverity.High, "A device is offline.");
    public static readonly ErrorCode TransactionStateInvalid = new("ATM-5001", ErrorSeverity.Medium, "The transaction is not in a valid state for this operation.");

    public static IReadOnlyList<ErrorCode> All { get; } =
    [
        Unknown,
        InvalidConfiguration,
        DeviceFault,
        DeviceOffline,
        TransactionStateInvalid,
    ];

    public static ErrorCode? Find(string code) =>
        All.FirstOrDefault(e => string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase));
}
