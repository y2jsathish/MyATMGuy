using System.Security.Cryptography;
using ATM.Application.Correlation;
using ATM.Application.Terminal;
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
    Task<AuditEntry> RecordAsync(string actor, string action, string target, string outcome, AuditCategory category = AuditCategory.System, CorrelationId? correlationId = null, CancellationToken cancellationToken = default);

    Task<AuditVerification> VerifyAsync(CancellationToken cancellationToken = default);
}

/// <summary>Key used to sign the audit chain. Production keys come from the key provider; the key never appears in config as plaintext.</summary>
public sealed class AuditSigningKey(byte[] key)
{
    public ReadOnlyMemory<byte> Value { get; } = key.Length >= 16 ? key : throw new ArgumentException("Audit key must be at least 16 bytes.", nameof(key));
}

/// <summary>Chains entries with HMAC-SHA256 so any edit, removal or reordering of stored history is detectable without the key.</summary>
public sealed class AuditService(IAuditStore store, TimeProvider clock, AuditSigningKey key, TerminalInfo terminal) : IAuditService
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<AuditEntry> RecordAsync(string actor, string action, string target, string outcome, AuditCategory category = AuditCategory.System, CorrelationId? correlationId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await store.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            var previous = existing.Count == 0 ? null : existing[^1].Hash;
            var entry = new AuditEntry(Guid.NewGuid(), clock.GetUtcNow(), terminal.TerminalId, category, actor, action, target ?? string.Empty, outcome ?? string.Empty, correlationId ?? CorrelationContext.Current, previous, null);
            entry = entry with { Hash = ComputeHash(entry, key) };
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
            if (entry.PreviousHash != previous || entry.Hash != ComputeHash(entry, key))
            {
                return new AuditVerification(false, entries.Count, i);
            }

            previous = entry.Hash;
        }

        return new AuditVerification(true, entries.Count, null);
    }

    public static string ComputeHash(AuditEntry entry, AuditSigningKey key)
    {
        var text = string.Join(
            '\n',
            entry.Id.ToString("N"),
            entry.TimestampUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            entry.Terminal,
            entry.Category.ToString(),
            entry.Actor,
            entry.Action,
            entry.Target,
            entry.Outcome,
            entry.CorrelationId?.ToString() ?? string.Empty,
            entry.PreviousHash ?? string.Empty);
        return Convert.ToHexString(HMACSHA256.HashData(key.Value.Span, Encoding.UTF8.GetBytes(text)));
    }
}
