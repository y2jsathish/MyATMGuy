using System.Text.Json;
using ATM.Application.Audit;
using ATM.Core.Entities;

namespace ATM.Infrastructure.Audit;

public sealed class InMemoryAuditStore : IAuditStore
{
    private readonly List<AuditEntry> entries = [];

    public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        lock (entries)
        {
            entries.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        lock (entries)
        {
            return Task.FromResult<IReadOnlyList<AuditEntry>>([.. entries]);
        }
    }
}

public sealed class AuditFileOptions
{
    public string Path { get; set; } = System.IO.Path.Combine(AppContext.BaseDirectory, "logs", "audit.jsonl");
}

/// <summary>One JSON object per line, append only. Survives restarts; tampering is caught by the hash chain.</summary>
public sealed class JsonLinesAuditStore(AuditFileOptions options) : IAuditStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly object sync = new();

    public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        var line = JsonSerializer.Serialize(entry, Json);
        lock (sync)
        {
            var directory = System.IO.Path.GetDirectoryName(options.Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(options.Path, line + Environment.NewLine);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            if (!File.Exists(options.Path))
            {
                return Task.FromResult<IReadOnlyList<AuditEntry>>([]);
            }

            var entries = File.ReadAllLines(options.Path)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonSerializer.Deserialize<AuditEntry>(l, Json)!)
                .ToList();
            return Task.FromResult<IReadOnlyList<AuditEntry>>(entries);
        }
    }
}
