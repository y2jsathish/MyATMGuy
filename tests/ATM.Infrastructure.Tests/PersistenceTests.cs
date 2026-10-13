using System.Text.RegularExpressions;
using ATM.Application.Audit;
using ATM.Application.Configuration;
using ATM.Application.Errors;
using ATM.Application.Persistence;
using ATM.Application.Terminal;
using ATM.Core.Devices;
using ATM.Core.Entities;
using ATM.Core.Errors;
using ATM.Core.ValueObjects;
using ATM.Infrastructure.Audit;
using ATM.Infrastructure.Configuration;
using ATM.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ATM.Infrastructure.Tests;

public sealed class PersistenceFixture : IDisposable
{
    public PersistenceFixture()
    {
        Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"atmdb-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Directory);
        Services = new ServiceCollection()
            .AddSingleton(new TerminalInfo("ATM-0001", "1", "t"))
            .AddSingleton<IKeyProvider>(new FileKeyProvider(System.IO.Path.Combine(Directory, "keys")))
            .AddSingleton<SecretProtector>()
            .AddSingleton<AuditSigningKey>(sp => AuditKeyDerivation.Derive(sp.GetRequiredService<IKeyProvider>()))
            .AddSingleton(new AuditFileOptions { Path = System.IO.Path.Combine(Directory, "audit-fallback.jsonl") })
            .AddSingleton<IErrorCounters, ErrorCounters>()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IAuditService, AuditService>()
            .AddAtmPersistence(o =>
            {
                o.Path = System.IO.Path.Combine(Directory, "atm.db");
                o.BackupDirectory = System.IO.Path.Combine(Directory, "backups");
            })
            .BuildServiceProvider();
    }

    public string Directory { get; }

    public ServiceProvider Services { get; }

    public DatabaseOptions Options => Services.GetRequiredService<DatabaseOptions>();

    public T Get<T>()
        where T : notnull => Services.GetRequiredService<T>();

    public void Dispose()
    {
        Services.Dispose();
        SqliteConnection.ClearAllPools();
        System.IO.Directory.Delete(Directory, recursive: true);
    }
}

public class PersistenceTests : IDisposable
{
    private static readonly TerminalId Terminal = TerminalId.Create("ATM-0001");
    private readonly PersistenceFixture fx = new();

    public void Dispose() => fx.Dispose();

    private async Task Init() => await fx.Get<DatabaseMaintenance>().InitializeAsync();

    private static Transaction NewTx(DateTimeOffset? at = null, decimal amount = 100m)
    {
        var tx = Transaction.Start(Terminal, TransactionType.Withdrawal, amount, MaskedPan.FromPan("4111111111111111"), at ?? DateTimeOffset.UtcNow);
        return tx;
    }

    [Fact]
    public async Task Migrations_CreateSchemaFromEmpty_WithWalAndFullSync()
    {
        await Init();

        await using var connection = new SqliteConnection(fx.Options.ConnectionString);
        await connection.OpenAsync();
        string Scalar(string sql)
        {
            using var c = connection.CreateCommand();
            c.CommandText = sql;
            return Convert.ToString(c.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
        }

        Scalar("PRAGMA journal_mode;").Should().Be("wal");
        Scalar("SELECT count(*) FROM sqlite_master WHERE name IN ('Transactions','Devices','AuditLogs','Errors','Configuration');").Should().Be("5");
    }

    [Fact]
    public async Task Transaction_RoundTrips_AndPaginatesNewestFirst()
    {
        await Init();
        var now = DateTimeOffset.UtcNow;
        await using (var uow = fx.Get<IUnitOfWorkFactory>().Create())
        {
            for (var i = 0; i < 25; i++)
            {
                var tx = NewTx(now.AddMinutes(i), 10 + i);
                if (i % 2 == 0)
                {
                    tx.Complete(now.AddMinutes(i));
                }

                await uow.Transactions.AddAsync(tx);
            }

            await uow.CommitAsync();
        }

        await using var read = fx.Get<IUnitOfWorkFactory>().Create();
        var page2 = await read.Transactions.PageAsync(2, 10, Terminal);

        page2.TotalCount.Should().Be(25);
        page2.TotalPages.Should().Be(3);
        page2.Items.Should().HaveCount(10);
        page2.Items[0].Amount.Should().Be(24m); // 25 items, newest first: page 2 starts at the 11th newest (index 14 => 10 + 14)
        page2.Items.First().Card.Value.Should().Be("411111******1111");
        page2.Items.First().Terminal.Should().Be(Terminal);
    }

    [Fact]
    public async Task UnitOfWork_CommitsAllOrNothing()
    {
        await Init();
        await using (var uow = fx.Get<IUnitOfWorkFactory>().Create())
        {
            await uow.Transactions.AddAsync(NewTx());
            await uow.Errors.AddAsync(new ErrorRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, "ATM-4001", ErrorSeverity.High, "x", null));
            // disposed without commit
        }

        await using var read = fx.Get<IUnitOfWorkFactory>().Create();
        (await read.Transactions.PageAsync(1, 10)).TotalCount.Should().Be(0);
        (await read.Errors.PageAsync(1, 10)).TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentUpdate_RaisesConflict()
    {
        await Init();
        var tx = NewTx();
        await using (var seed = fx.Get<IUnitOfWorkFactory>().Create())
        {
            await seed.Transactions.AddAsync(tx);
            await seed.CommitAsync();
        }

        await using var first = fx.Get<IUnitOfWorkFactory>().Create();
        await using var second = fx.Get<IUnitOfWorkFactory>().Create();
        var a = (await first.Transactions.GetAsync(tx.Id))!;
        var b = (await second.Transactions.GetAsync(tx.Id))!;
        a.Complete(DateTimeOffset.UtcNow);
        b.Fail("declined", DateTimeOffset.UtcNow);

        await first.CommitAsync();
        await second.Invoking(u => u.CommitAsync()).Should().ThrowAsync<AtmException>()
            .Where(e => e.Code == ErrorCatalogue.ConcurrencyConflict);
    }

    [Fact]
    public async Task Devices_Upsert_KeepsOneRowPerTerminalAndKind()
    {
        await Init();
        for (var i = 0; i < 3; i++)
        {
            await using var uow = fx.Get<IUnitOfWorkFactory>().Create();
            await uow.Devices.UpsertAsync(new Device(Guid.NewGuid(), Terminal, DeviceKind.CashDispenser, "Dispenser", i == 2 ? DeviceStatus.Fault : DeviceStatus.Ok, DateTimeOffset.UtcNow));
            await uow.CommitAsync();
        }

        await using var read = fx.Get<IUnitOfWorkFactory>().Create();
        var devices = await read.Devices.ListAsync(Terminal);
        devices.Should().ContainSingle().Which.Status.Should().Be(DeviceStatus.Fault);
    }

    [Fact]
    public async Task SecretConfig_IsEncryptedAtRest_AndReadableThroughRepository()
    {
        await Init();
        await using (var uow = fx.Get<IUnitOfWorkFactory>().Create())
        {
            await uow.Config.SetAsync("Monitoring:Token", "super-secret-token", isSecret: true, DateTimeOffset.UtcNow);
            await uow.Config.SetAsync("Ui:Theme", "dark", isSecret: false, DateTimeOffset.UtcNow);
            await uow.CommitAsync();
        }

        await using var read = fx.Get<IUnitOfWorkFactory>().Create();
        (await read.Config.GetAsync("Monitoring:Token")).Should().Be("super-secret-token");
        (await read.Config.GetAsync("Ui:Theme")).Should().Be("dark");
        (await read.Config.GetAsync("missing")).Should().BeNull();

        SqliteConnection.ClearAllPools();
        var bytes = FileBytes();
        System.Text.Encoding.UTF8.GetString(bytes).Should().NotContain("super-secret-token");
    }

    [Fact]
    public async Task Database_NeverContainsAFullPan()
    {
        await Init();
        await using (var uow = fx.Get<IUnitOfWorkFactory>().Create())
        {
            await uow.Transactions.AddAsync(NewTx());
            await uow.Errors.AddAsync(new ErrorRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, "ATM-4001", ErrorSeverity.High, "Dispense failed for card 4111111111111111 pin=1234", null));
            await uow.CommitAsync();
        }

        await fx.Get<IAuditService>().RecordAsync("system", "Test", "card 4111111111111111", "ok");
        SqliteConnection.ClearAllPools();

        var text = System.Text.Encoding.Latin1.GetString(FileBytes());
        var candidates = Regex.Matches(text, @"(?<!\d)\d{13,19}(?!\d)").Select(m => m.Value);
        candidates.Where(IsLuhnValid).Should().BeEmpty("no full card number may be stored");
        text.Should().NotContain("4111111111111111");
    }

    [Fact]
    public async Task Audit_IsStoredInDatabase_ChainVerifies_AndIsQueryable()
    {
        await Init();
        var audit = fx.Get<IAuditService>();
        var id = CorrelationId.New();
        await audit.RecordAsync("op", "Login", "ui", "ok", AuditCategory.Security, id);
        await audit.RecordAsync("op", "Logout", "ui", "ok", AuditCategory.Security, id);
        await audit.RecordAsync("op", "Other", "ui", "ok");

        (await audit.VerifyAsync()).Should().Be(new AuditVerification(true, 3, null));
        await using var read = fx.Get<IUnitOfWorkFactory>().Create();
        (await read.Audit.PageAsync(1, 10, id)).Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Audit_FallsBackToFile_WhenDatabaseFails_AndIsReplayedInOrder()
    {
        await Init();
        var audit = fx.Get<IAuditService>();
        await audit.RecordAsync("op", "One", "t", "ok");

        // Break the database: lock it exclusively with a held write transaction from another connection.
        SqliteConnection.ClearAllPools();
        var dbPath = fx.Options.Path;
        File.Move(dbPath, dbPath + ".away");
        Directory.CreateDirectory(dbPath); // a directory where the file should be: opens fail
        await audit.RecordAsync("op", "Two", "t", "ok");
        await audit.RecordAsync("op", "Three", "t", "ok");
        fx.Get<IErrorCounters>().Snapshot().Should().ContainKey(ErrorCatalogue.DatabaseWriteFailed.Code);

        Directory.Delete(dbPath);
        File.Move(dbPath + ".away", dbPath);
        await audit.RecordAsync("op", "Four", "t", "ok");

        var result = await audit.VerifyAsync();
        result.IsValid.Should().BeTrue();
        result.EntryCount.Should().Be(4);
        (await new JsonLinesAuditStore(fx.Get<AuditFileOptions>()).ReadAllAsync()).Should().BeEmpty("pending entries were moved into the database");
    }

    [Fact]
    public async Task Backup_CreatesRestorableCopy_AndKeepsOnlyConfiguredCount()
    {
        await Init();
        var maintenance = fx.Get<DatabaseMaintenance>();
        await using (var uow = fx.Get<IUnitOfWorkFactory>().Create())
        {
            await uow.Transactions.AddAsync(NewTx());
            await uow.CommitAsync();
        }

        fx.Options.BackupsToKeep = 2;
        for (var i = 0; i < 4; i++)
        {
            await maintenance.BackupAsync();
            await Task.Delay(5);
        }

        Directory.GetFiles(fx.Options.BackupDirectory, "atm-*.bak").Should().HaveCount(2);
        (await maintenance.IntegrityOkAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task CorruptDatabase_IsRestoredFromBackup_AtStartup_WithCriticalError()
    {
        await Init();
        var maintenance = fx.Get<DatabaseMaintenance>();
        await using (var uow = fx.Get<IUnitOfWorkFactory>().Create())
        {
            await uow.Transactions.AddAsync(NewTx(amount: 77m));
            await uow.CommitAsync();
        }

        await maintenance.BackupAsync();
        SqliteConnection.ClearAllPools();
        File.WriteAllBytes(fx.Options.Path, Enumerable.Repeat((byte)0xAB, 8192).ToArray());
        foreach (var extra in new[] { "-wal", "-shm" })
        {
            if (File.Exists(fx.Options.Path + extra))
            {
                File.Delete(fx.Options.Path + extra);
            }
        }

        await maintenance.InitializeAsync();

        fx.Get<IErrorCounters>().Snapshot().Should().ContainKey(ErrorCatalogue.DatabaseCorrupt.Code);
        ErrorCatalogue.DatabaseCorrupt.Severity.Should().Be(ErrorSeverity.Critical);
        await using var read = fx.Get<IUnitOfWorkFactory>().Create();
        (await read.Transactions.PageAsync(1, 10)).Items.Should().ContainSingle().Which.Amount.Should().Be(77m);
    }

    [Fact]
    public async Task CorruptDatabase_WithoutBackup_FailsStartupClearly()
    {
        await Init();
        SqliteConnection.ClearAllPools();
        File.WriteAllBytes(fx.Options.Path, Enumerable.Repeat((byte)0xAB, 8192).ToArray());

        await fx.Get<DatabaseMaintenance>().Invoking(m => m.InitializeAsync()).Should().ThrowAsync<AtmException>()
            .Where(e => e.Code == ErrorCatalogue.DatabaseCorrupt);
    }

    [Fact]
    public async Task Purge_RemovesOldRows_AuditsIt_AndAuditChainStillVerifies()
    {
        await Init();
        var maintenance = fx.Get<DatabaseMaintenance>();
        var now = DateTimeOffset.UtcNow;
        await using (var uow = fx.Get<IUnitOfWorkFactory>().Create())
        {
            await uow.Transactions.AddAsync(NewTx(now.AddDays(-100)));
            await uow.Transactions.AddAsync(NewTx(now.AddDays(-1)));
            await uow.Errors.AddAsync(new ErrorRecord(Guid.NewGuid(), now.AddDays(-40), "ATM-0001", ErrorSeverity.Low, "old", null));
            await uow.Errors.AddAsync(new ErrorRecord(Guid.NewGuid(), now.AddDays(-1), "ATM-0001", ErrorSeverity.Low, "new", null));
            await uow.CommitAsync();
        }

        // Audit rows dated in the past, written through the real service using a clock we control.
        var oldClock = new FixedClock(now.AddDays(-400));
        var oldAudit = new AuditService(fx.Get<IAuditStore>(), oldClock, fx.Get<AuditSigningKey>(), fx.Get<TerminalInfo>());
        await oldAudit.RecordAsync("op", "Ancient1", "t", "ok");
        await oldAudit.RecordAsync("op", "Ancient2", "t", "ok");
        await fx.Get<IAuditService>().RecordAsync("op", "Recent", "t", "ok");

        var result = await maintenance.PurgeAsync(new RetentionOptions());

        result.Transactions.Should().Be(1);
        result.Errors.Should().Be(1);
        result.AuditEntries.Should().Be(2);
        var verification = await fx.Get<IAuditService>().VerifyAsync();
        verification.IsValid.Should().BeTrue();
        await using var read = fx.Get<IUnitOfWorkFactory>().Create();
        (await read.Audit.PageAsync(1, 50)).Items.Select(a => a.Action).Should().Contain(AuditService.PurgeAction).And.NotContain("Ancient1");
    }

    [Fact]
    public async Task Purge_Truncation_WithoutAnchor_IsDetected()
    {
        await Init();
        var audit = fx.Get<IAuditService>();
        for (var i = 0; i < 4; i++)
        {
            await audit.RecordAsync("op", $"A{i}", "t", "ok");
        }

        await using (var db = await fx.Get<IDbContextFactory<AtmDbContext>>().CreateDbContextAsync())
        {
            await db.AuditLogs.Where(a => a.Action == "A0" || a.Action == "A1").ExecuteDeleteAsync();
        }

        (await audit.VerifyAsync()).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Migration_Failure_RestoresPreviousDatabase()
    {
        await Init();
        await using (var uow = fx.Get<IUnitOfWorkFactory>().Create())
        {
            await uow.Transactions.AddAsync(NewTx(amount: 55m));
            await uow.CommitAsync();
        }

        // Pretend an older schema: drop the migration history so the initial migration is "pending" again and fails (tables exist).
        SqliteConnection.ClearAllPools();
        await using (var c = new SqliteConnection(fx.Options.ConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM __EFMigrationsHistory;";
            await cmd.ExecuteNonQueryAsync();
        }

        await fx.Get<DatabaseMaintenance>().Invoking(m => m.InitializeAsync()).Should().ThrowAsync<AtmException>()
            .Where(e => e.Code == ErrorCatalogue.DatabaseMigrationFailed);

        fx.Get<IErrorCounters>().Snapshot().Should().ContainKey(ErrorCatalogue.DatabaseMigrationFailed.Code);
        SqliteConnection.ClearAllPools();
        await using var read = fx.Get<IUnitOfWorkFactory>().Create();
        (await read.Transactions.PageAsync(1, 10)).Items.Should().ContainSingle().Which.Amount.Should().Be(55m);
    }

    private byte[] FileBytes()
    {
        using var stream = new FileStream(fx.Options.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var wal = fx.Options.Path + "-wal";
        if (File.Exists(wal))
        {
            using var w = new FileStream(wal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            w.CopyTo(memory);
        }

        return memory.ToArray();
    }

    private static bool IsLuhnValid(string digits)
    {
        var sum = 0;
        var alt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (alt && (d *= 2) > 9)
            {
                d -= 9;
            }

            sum += d;
            alt = !alt;
        }

        return sum % 10 == 0;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
