namespace ATM.Application.Monitoring;

/// <summary>Wire contracts between a terminal and the central monitoring API (v1). Add optional properties only; never rename or remove.</summary>
public sealed record HeartbeatRequest(string TerminalId, string Version, string Machine, string Environment, DateTimeOffset SentUtc, long UptimeSeconds, int ErrorTotal);

public sealed record HealthEntryDto(string Name, string State, string Message, DateTimeOffset LastRunUtc);

public sealed record HealthRequest(string TerminalId, string Overall, IReadOnlyList<HealthEntryDto> Entries, DateTimeOffset SentUtc);

public sealed record ErrorRequest(string TerminalId, string Code, string Severity, string Message, Guid? CorrelationId, DateTimeOffset OccurredUtc);

public sealed record TerminalSummary(string TerminalId, string Version, string Machine, string Environment, DateTimeOffset LastHeartbeatUtc, string? Overall, int ErrorTotal);

public interface IMonitoringClient
{
    /// <summary>Sends now or queues for later. Returns true when the server accepted it immediately.</summary>
    Task<bool> SendHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default);

    Task<bool> SendHealthAsync(HealthRequest request, CancellationToken cancellationToken = default);

    Task<bool> SendErrorAsync(ErrorRequest request, CancellationToken cancellationToken = default);

    /// <summary>Sends queued items oldest first and stops at the first failure. Returns how many were sent.</summary>
    Task<int> ReplayQueuedAsync(CancellationToken cancellationToken = default);

    int QueuedCount { get; }
}
