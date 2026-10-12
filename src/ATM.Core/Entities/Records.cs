using ATM.Core.Devices;
using ATM.Core.Errors;
using ATM.Core.ValueObjects;

namespace ATM.Core.Entities;

/// <summary>A device known to the terminal and its last reported state.</summary>
public sealed record Device(Guid Id, TerminalId Terminal, DeviceKind Kind, string Name, DeviceStatus Status, DateTimeOffset LastSeenUtc);

/// <summary>One audit trail entry. Hash fields are filled by the audit engine in Sprint 2.</summary>
public sealed record AuditEntry(
    Guid Id,
    DateTimeOffset TimestampUtc,
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
