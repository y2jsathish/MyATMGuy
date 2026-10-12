using System.ComponentModel.DataAnnotations;
using ATM.Application.Audit;
using ATM.Application.Errors;
using ATM.Application.Persistence;
using ATM.Core.Entities;
using ATM.Core.Errors;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace ATM.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    [Required]
    public string Path { get; set; } = System.IO.Path.Combine(AppContext.BaseDirectory, "data", "atm.db");

    [Required]
    public string BackupDirectory { get; set; } = System.IO.Path.Combine(AppContext.BaseDirectory, "data", "backups");

    [Range(100, 60000)]
    public int BusyTimeoutMs { get; set; } = 5000;

    [Range(1, 365)]
    public int BackupsToKeep { get; set; } = 7;

    public string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = Path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private,
        DefaultTimeout = Math.Max(1, BusyTimeoutMs / 1000),
        Pooling = true,
    }.ToString();
}

/// <summary>Applies the ATM durability settings every time a SQLite connection opens: WAL journal, synchronous FULL, busy timeout.</summary>
public sealed class SqlitePragmaInterceptor(DatabaseOptions options) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Apply(connection);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Apply(connection);
        return Task.CompletedTask;
    }

    private void Apply(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout={options.BusyTimeoutMs};";
        command.ExecuteNonQuery();
    }
}

public sealed class EfAuditStore(IDbContextFactory<AtmDbContext> contexts) : IAuditStore
{
    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.AuditLogs.Add(entry);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AuditEntry?> GetLastAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.AuditLogs.AsNoTracking().OrderByDescending(a => EF.Property<long>(a, AtmDbContext.Sequence)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditEntry>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.AuditLogs.AsNoTracking().OrderBy(a => EF.Property<long>(a, AtmDbContext.Sequence)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Writes audit records to the database. If the database is unavailable the record goes to a file instead, and is moved
/// into the database (in order) the next time a write succeeds, so the hash chain stays whole.
/// </summary>
public sealed class FallbackAuditStore(IAuditStore primary, Audit.JsonLinesAuditStore fallback, IErrorCounters counters) : IAuditStore
{
    private AuditEntry? lastKnown;

    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        lastKnown = entry;
        try
        {
            await FlushPendingAsync(cancellationToken).ConfigureAwait(false);
            await primary.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbUpdateException or SqliteException or InvalidOperationException or IOException)
        {
            counters.Record(ErrorCatalogue.DatabaseWriteFailed);
            await fallback.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<AuditEntry?> GetLastAsync(CancellationToken cancellationToken = default)
    {
        var pending = await fallback.GetLastAsync(cancellationToken).ConfigureAwait(false);
        if (pending is not null)
        {
            return pending;
        }

        try
        {
            return lastKnown = await primary.GetLastAsync(cancellationToken).ConfigureAwait(false) ?? lastKnown;
        }
        catch (Exception ex) when (ex is DbUpdateException or SqliteException or InvalidOperationException)
        {
            // Database unavailable: continue the chain from the newest entry this process wrote.
            return lastKnown;
        }
    }

    public async Task<IReadOnlyList<AuditEntry>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        var stored = new List<AuditEntry>();
        try
        {
            stored.AddRange(await primary.ReadAllAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is DbUpdateException or SqliteException or InvalidOperationException)
        {
            // Database unavailable: report only what the fallback holds.
        }

        var known = stored.Select(e => e.Id).ToHashSet();
        stored.AddRange((await fallback.ReadAllAsync(cancellationToken).ConfigureAwait(false)).Where(e => !known.Contains(e.Id)));
        return stored;
    }

    private async Task FlushPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await fallback.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return;
        }

        foreach (var entry in pending)
        {
            await primary.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        await fallback.ClearAsync().ConfigureAwait(false);
    }
}

/// <summary>Startup checks, backups, safe migration and retention purge for the local SQLite database.</summary>
public sealed class DatabaseMaintenance(
    IDbContextFactory<AtmDbContext> contexts,
    DatabaseOptions options,
    IErrorCounters counters,
    IAuditService audit,
    TimeProvider clock)
{
    /// <summary>Integrity check (restoring the newest backup on corruption), then migrate with a backup taken first.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(options.Path)!);
        Directory.CreateDirectory(options.BackupDirectory);

        if (File.Exists(options.Path) && !await IntegrityOkAsync(cancellationToken).ConfigureAwait(false))
        {
            counters.Record(ErrorCatalogue.DatabaseCorrupt);
            var backup = LatestBackup() ?? throw new AtmException(ErrorCatalogue.DatabaseCorrupt, "The database is corrupt and no backup exists.");
            Restore(backup);
            await audit.RecordAsync("system", "DatabaseRestored", options.Path, $"integrity check failed; restored {System.IO.Path.GetFileName(backup)}", AuditCategory.System, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var existed = File.Exists(options.Path);
        var backupPath = existed ? await BackupAsync(cancellationToken).ConfigureAwait(false) : null;
        try
        {
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            counters.Record(ErrorCatalogue.DatabaseMigrationFailed);
            SqliteConnection.ClearAllPools();
            if (backupPath is not null)
            {
                Restore(backupPath);
            }
            else if (File.Exists(options.Path))
            {
                File.Delete(options.Path);
            }

            throw new AtmException(ErrorCatalogue.DatabaseMigrationFailed, $"Migration {string.Join(", ", pending)} failed; the previous database was restored.", ex);
        }
    }

    public async Task<bool> IntegrityOkAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqliteConnection(options.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            return string.Equals((string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>Online backup (safe while the terminal is running). Keeps the newest <see cref="DatabaseOptions.BackupsToKeep"/> files.</summary>
    public async Task<string> BackupAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(options.BackupDirectory);
        var target = System.IO.Path.Combine(options.BackupDirectory, $"atm-{clock.GetUtcNow():yyyyMMddHHmmssfff}.bak");

        await using (var source = new SqliteConnection(options.ConnectionString))
        await using (var destination = new SqliteConnection($"Data Source={target}"))
        {
            await source.OpenAsync(cancellationToken).ConfigureAwait(false);
            await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
            source.BackupDatabase(destination);
        }

        SqliteConnection.ClearAllPools();
        foreach (var old in Directory.GetFiles(options.BackupDirectory, "atm-*.bak").OrderByDescending(f => f, StringComparer.Ordinal).Skip(options.BackupsToKeep))
        {
            File.Delete(old);
        }

        return target;
    }

    public void Restore(string backupPath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            if (File.Exists(options.Path + suffix))
            {
                File.Delete(options.Path + suffix);
            }
        }

        File.Copy(backupPath, options.Path);
    }

    public string? LatestBackup() =>
        Directory.Exists(options.BackupDirectory)
            ? Directory.GetFiles(options.BackupDirectory, "atm-*.bak").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault()
            : null;

    /// <summary>Deletes data older than the retention windows and audits what was removed. Audit entries are removed oldest first behind a recorded anchor, so the chain still verifies.</summary>
    public async Task<PurgeResult> PurgeAsync(RetentionOptions retention, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var txCutoff = now.AddDays(-retention.TransactionDays);
        var errCutoff = now.AddDays(-retention.ErrorDays);
        var auditCutoff = now.AddDays(-retention.AuditDays);

        var transactions = await db.Transactions.Where(t => t.CreatedUtc < txCutoff).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var errors = await db.Errors.Where(e => e.TimestampUtc < errCutoff).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        var auditRemoved = 0;
        var lastOld = await db.AuditLogs.AsNoTracking()
            .Where(a => a.TimestampUtc < auditCutoff)
            .OrderByDescending(a => EF.Property<long>(a, AtmDbContext.Sequence))
            .Select(a => new { Seq = EF.Property<long>(a, AtmDbContext.Sequence), a.Hash })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (lastOld is not null)
        {
            var count = await db.AuditLogs.CountAsync(a => EF.Property<long>(a, AtmDbContext.Sequence) <= lastOld.Seq, cancellationToken).ConfigureAwait(false);
            await audit.RecordAsync("system", AuditService.PurgeAction, "AuditLogs", $"anchor={lastOld.Hash}; removed={count}", AuditCategory.System, cancellationToken: cancellationToken).ConfigureAwait(false);
            auditRemoved = await db.AuditLogs.Where(a => EF.Property<long>(a, AtmDbContext.Sequence) <= lastOld.Seq).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (transactions + errors > 0)
        {
            await audit.RecordAsync("system", "RetentionPurge", "Transactions,Errors", $"transactions={transactions}; errors={errors}", AuditCategory.System, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return new PurgeResult(transactions, errors, auditRemoved);
    }
}
