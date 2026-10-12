using System.Security.Cryptography;
using System.Text;
using ATM.Core.Entities;
using ATM.Core.ValueObjects;

namespace ATM.Application.Audit;

/// <summary>Append-only storage for audit entries. Implementations live in Infrastructure.</summary>
public interface IAuditStore
{
    Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditEntry>> ReadAllAsync(CancellationToken cancellationToken = default);
}

public sealed record AuditVerification(bool IsValid, int EntryCount, int? FirstBrokenIndex);

public interface IAuditService
{
    Task<AuditEntry> RecordAsync(string actor, string action, string target, string outcome, CorrelationId? correlationId = null, CancellationToken cancellationToken = default);

    Task<AuditVerification> VerifyAsync(CancellationToken cancellationToken = default);
}

/// <summary>Hash-chains entries so any edit, removal or reordering of stored history is detectable.</summary>
public sealed class AuditService(IAuditStore store, TimeProvider clock) : IAuditService
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<AuditEntry> RecordAsync(string actor, string action, string target, string outcome, CorrelationId? correlationId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await store.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            var previous = existing.Count == 0 ? null : existing[^1].Hash;
            var entry = new AuditEntry(Guid.NewGuid(), clock.GetUtcNow(), actor, action, target ?? string.Empty, outcome ?? string.Empty, correlationId, previous, null);
            entry = entry with { Hash = ComputeHash(entry) };
            await store.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
            return entry;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AuditVerification> VerifyAsync(CancellationToken cancellationToken = default)
    {
        var entries = await store.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        string? previous = null;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.PreviousHash != previous || entry.Hash != ComputeHash(entry))
            {
                return new AuditVerification(false, entries.Count, i);
            }

            previous = entry.Hash;
        }

        return new AuditVerification(true, entries.Count, null);
    }

    public static string ComputeHash(AuditEntry entry)
    {
        var text = string.Join(
            '\n',
            entry.Id.ToString("N"),
            entry.TimestampUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            entry.Actor,
            entry.Action,
            entry.Target,
            entry.Outcome,
            entry.CorrelationId?.ToString() ?? string.Empty,
            entry.PreviousHash ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
