using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ATM.Application.Audit;
using ATM.Application.Errors;
using ATM.Core.Entities;
using ATM.Core.Errors;

namespace ATM.Application.Configuration;

public static partial class SecretKeys
{
    [GeneratedRegex(@"(password|passwd|pwd|secret|token|apikey|api[-_]?key|private[-_]?key|signingkey|connectionstring)$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretNamePattern();

    public static bool IsSecretKey(string key) => SecretNamePattern().IsMatch(key);

    /// <summary>Keys that look like secrets but hold a readable value. Prod refuses to start with any.</summary>
    public static IReadOnlyList<string> FindPlaintext(IReadOnlyDictionary<string, string?> flat) =>
        [.. flat.Where(kv => IsSecretKey(kv.Key) && !string.IsNullOrEmpty(kv.Value) && !SecretProtector.IsProtected(kv.Value)).Select(kv => kv.Key)];
}

public sealed record ConfigChange(string Key, string? OldValue, string? NewValue, bool IsSecret);

public static class ConfigDiff
{
    public static IReadOnlyList<ConfigChange> Compare(IReadOnlyDictionary<string, string?> before, IReadOnlyDictionary<string, string?> after)
    {
        var changes = new List<ConfigChange>();
        foreach (var key in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            before.TryGetValue(key, out var oldValue);
            after.TryGetValue(key, out var newValue);
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                changes.Add(new ConfigChange(key, oldValue, newValue, SecretKeys.IsSecretKey(key) || SecretProtector.IsProtected(newValue) || SecretProtector.IsProtected(oldValue)));
            }
        }

        return changes;
    }

    public static string Hash(string? value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)))[..16];
}

public sealed record ConfigApplyResult(bool Applied, IReadOnlyList<string> Errors, IReadOnlyList<ConfigChange> Changes);

/// <summary>
/// Accepts configuration candidates. A valid one becomes current and the last known good; an invalid one is rejected, audited,
/// counted as a High error, and the last known good stays in force. Every change is audited; secrets are recorded as hashes only.
/// </summary>
public sealed class ConfigGovernor(
    Func<IReadOnlyDictionary<string, string?>, IReadOnlyList<string>> validate,
    IAuditService audit,
    IErrorCounters counters,
    Action<IReadOnlyDictionary<string, string?>>? persistLastKnownGood = null)
{
    private readonly object sync = new();
    private IReadOnlyDictionary<string, string?> current = new Dictionary<string, string?>();

    public IReadOnlyDictionary<string, string?> Current
    {
        get
        {
            lock (sync)
            {
                return current;
            }
        }
    }

    public void Initialize(IReadOnlyDictionary<string, string?> snapshot)
    {
        lock (sync)
        {
            current = new Dictionary<string, string?>(snapshot);
        }
    }

    public async Task<ConfigApplyResult> ApplyAsync(IReadOnlyDictionary<string, string?> candidate, string source, CancellationToken cancellationToken = default)
    {
        var errors = validate(candidate);
        if (errors.Count > 0)
        {
            counters.Record(ErrorCatalogue.ConfigurationRejected);
            await audit.RecordAsync("system", "ConfigRejected", source, string.Join("; ", errors), AuditCategory.Config, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new ConfigApplyResult(false, errors, []);
        }

        IReadOnlyList<ConfigChange> changes;
        lock (sync)
        {
            changes = ConfigDiff.Compare(current, candidate);
            current = new Dictionary<string, string?>(candidate);
        }

        foreach (var change in changes)
        {
            var outcome = change.IsSecret
                ? $"changed {ConfigDiff.Hash(change.OldValue)} -> {ConfigDiff.Hash(change.NewValue)}"
                : $"changed '{change.OldValue}' -> '{change.NewValue}'";
            await audit.RecordAsync("system", "ConfigChanged", $"{change.Key} ({source})", outcome, AuditCategory.Config, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        persistLastKnownGood?.Invoke(candidate);
        return new ConfigApplyResult(true, [], changes);
    }
}

public sealed record IntegrityResult(bool IsValid, IReadOnlyList<string> Problems);

/// <summary>Records SHA-256 hashes of config files in a manifest and detects later edits.</summary>
public static class ConfigIntegrity
{
    public const string ManifestName = "config.manifest";

    public static void WriteManifest(string directory, IEnumerable<string> fileNames)
    {
        var lines = fileNames.Where(f => File.Exists(Path.Combine(directory, f))).OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => $"{HashFile(Path.Combine(directory, f))}  {f}");
        File.WriteAllLines(Path.Combine(directory, ManifestName), lines);
    }

    public static IntegrityResult Verify(string directory)
    {
        var manifest = Path.Combine(directory, ManifestName);
        if (!File.Exists(manifest))
        {
            return new IntegrityResult(false, [$"{ManifestName} is missing."]);
        }

        var problems = new List<string>();
        foreach (var line in File.ReadAllLines(manifest).Where(l => l.Length > 0))
        {
            var parts = line.Split("  ", 2);
            var path = Path.Combine(directory, parts[^1]);
            if (!File.Exists(path))
            {
                problems.Add($"{parts[^1]} is missing.");
            }
            else if (!string.Equals(HashFile(path), parts[0], StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{parts[^1]} does not match its recorded hash.");
            }
        }

        return new IntegrityResult(problems.Count == 0, problems);
    }

    public static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
