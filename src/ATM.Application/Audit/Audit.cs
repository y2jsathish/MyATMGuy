using System.Security.Cryptography;
using ATM.Application.Correlation;
using ATM.Application.Masking;
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

    /// <summary>The newest entry, or null when empty. Lets the chain grow without reading the whole history.</summary>
    Task<AuditEntry?> GetLastAsync(CancellationToken cancellationToken = default);
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
            var previous = (await store.GetLastAsync(cancellationToken).ConfigureAwait(false))?.Hash;
            var entry = new AuditEntry(Guid.NewGuid(), clock.GetUtcNow(), terminal.TerminalId, category, SensitiveDataMasker.Mask(actor), SensitiveDataMasker.Mask(action), SensitiveDataMasker.Mask(target), SensitiveDataMasker.Mask(outcome), correlationId ?? CorrelationContext.Current, previous, null);
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

        // After a retention purge the oldest stored entry points at a removed one. That is only legitimate when an
        // "AuditPurged" entry later in the chain records that exact hash as its anchor.
        string? previous = entries.Count > 0 && entries[0].PreviousHash is { } head && HasPurgeAnchor(entries, head) ? head : null;
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

    public const string PurgeAction = "AuditPurged";

    private static bool HasPurgeAnchor(IReadOnlyList<AuditEntry> entries, string head) =>
        entries.Any(e => e.Action == PurgeAction && e.Outcome.Contains($"anchor={head}", StringComparison.Ordinal));

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
