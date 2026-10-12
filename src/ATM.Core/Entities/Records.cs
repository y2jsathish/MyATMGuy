using ATM.Core.Devices;
using ATM.Core.Errors;
using ATM.Core.ValueObjects;

namespace ATM.Core.Entities;

/// <summary>A device known to the terminal and its last reported state.</summary>
public sealed record Device(Guid Id, TerminalId Terminal, DeviceKind Kind, string Name, DeviceStatus Status, DateTimeOffset LastSeenUtc);

public enum AuditCategory
{
    System,
    Security,
    Config,
    Operator,
}

/// <summary>One audit trail entry. <c>Hash</c> is an HMAC over the content and <c>PreviousHash</c>, filled by the audit service.</summary>
public sealed record AuditEntry(
    Guid Id,
    DateTimeOffset TimestampUtc,
    string Terminal,
    AuditCategory Category,
    string Actor,
    string Action,
    string Target,
    string Outcome,
    CorrelationId? CorrelationId,
    string? PreviousHash,
    string? Hash);

/// <summary>A captured error, stored for monitoring and support.</summary>
public sealed record ErrorRecord(
    Guid Id,
    DateTimeOffset TimestampUtc,
    string Code,
    ErrorSeverity Severity,
    string Message,
    CorrelationId? CorrelationId);

/// <summary>A configuration value. Secret values are stored encrypted and never logged.</summary>
public sealed record ConfigItem(string Key, string Value, bool IsSecret, DateTimeOffset UpdatedUtc);

/// <summary>An event that failed all delivery attempts, kept so it can be replayed.</summary>
public sealed record DeadLetter(Guid Id, string EventType, string Payload, string Handler, string Error, int Attempts, DateTimeOffset FailedUtc);
