using ATM.Core.Devices;
using ATM.Core.Entities;
using ATM.Core.Errors;
using ATM.Core.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ATM.Infrastructure.Persistence;

/// <summary>
/// EF Core mapping for the local ATM database. Core entities carry no EF attributes; everything is configured here.
/// Timestamps are stored as UTC ticks. Mutable tables carry a RowVersion concurrency token that SaveChanges increments.
/// </summary>
public sealed class AtmDbContext(DbContextOptions<AtmDbContext> options) : DbContext(options)
{
    public const string RowVersion = "RowVersion";
    public const string Sequence = "Seq";

    public DbSet<Transaction> Transactions => Set<Transaction>();

    public DbSet<Device> Devices => Set<Device>();

    public DbSet<AuditEntry> AuditLogs => Set<AuditEntry>();

    public DbSet<ErrorRecord> Errors => Set<ErrorRecord>();

    public DbSet<ConfigItem> Configuration => Set<ConfigItem>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToUtcTicks>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<NullableDateTimeOffsetToUtcTicks>();
        configurationBuilder.Properties<TerminalId>().HaveConversion<TerminalIdConverter>().HaveMaxLength(32);
        configurationBuilder.Properties<MaskedPan>().HaveConversion<MaskedPanConverter>().HaveMaxLength(19);
        configurationBuilder.Properties<CorrelationId?>().HaveConversion<NullableCorrelationIdConverter>().HaveMaxLength(32);
        configurationBuilder.Properties<ErrorSeverity>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<DeviceKind>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<DeviceStatus>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<TransactionType>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<TransactionStatus>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<AuditCategory>().HaveConversion<string>().HaveMaxLength(16);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Transaction>(b =>
        {
            b.ToTable("Transactions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Terminal);
            b.Property(x => x.Type);
            b.Property(x => x.Amount);
            b.Property(x => x.Card);
            b.Property(x => x.CreatedUtc);
            b.Property(x => x.Status);
            b.Property(x => x.CompletedUtc);
            b.Property<long>(RowVersion).IsConcurrencyToken();
            b.Property(x => x.FailureReason).HasMaxLength(512);
            b.HasIndex(x => x.CreatedUtc);
            b.HasIndex(x => x.Terminal);
            b.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<Device>(b =>
        {
            b.ToTable("Devices");
            b.HasKey(x => x.Id);
            b.Property<long>(RowVersion).IsConcurrencyToken();
            b.Property(x => x.Name).HasMaxLength(128);
            b.HasIndex(x => new { x.Terminal, x.Kind }).IsUnique();
        });

        modelBuilder.Entity<AuditEntry>(b =>
        {
            b.ToTable("AuditLogs");
            b.Property<long>(Sequence).ValueGeneratedOnAdd();
            b.HasKey(Sequence);
            b.HasIndex(x => x.Id).IsUnique();
            b.Property(x => x.Terminal).HasMaxLength(32);
            b.Property(x => x.Actor).HasMaxLength(128);
            b.Property(x => x.Action).HasMaxLength(128);
            b.Property(x => x.Target).HasMaxLength(512);
            b.Property(x => x.Outcome).HasMaxLength(2048);
            b.Property(x => x.PreviousHash).HasMaxLength(64);
            b.Property(x => x.Hash).HasMaxLength(64);
            b.HasIndex(x => x.TimestampUtc);
            b.HasIndex(x => x.CorrelationId);
            b.HasIndex(x => x.Terminal);
        });

        modelBuilder.Entity<ErrorRecord>(b =>
        {
            b.ToTable("Errors");
            b.HasKey(x => x.Id);
            b.Property(x => x.Code).HasMaxLength(16);
            b.Property(x => x.Message).HasMaxLength(2048);
            b.HasIndex(x => x.TimestampUtc);
            b.HasIndex(x => x.CorrelationId);
        });

        modelBuilder.Entity<ConfigItem>(b =>
        {
            b.ToTable("Configuration");
            b.HasKey(x => x.Key);
            b.Property<long>(RowVersion).IsConcurrencyToken();
            b.Property(x => x.Key).HasMaxLength(256);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BumpRowVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BumpRowVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void BumpRowVersions()
    {
        foreach (var entry in ChangeTracker.Entries().Where(e => e.State == EntityState.Modified && e.Metadata.FindProperty(RowVersion) is not null))
        {
            var property = entry.Property(RowVersion);
            property.CurrentValue = (long)property.OriginalValue! + 1;
        }
    }

    private sealed class DateTimeOffsetToUtcTicks() : ValueConverter<DateTimeOffset, long>(
        v => v.UtcTicks,
        v => new DateTimeOffset(v, TimeSpan.Zero));

    private sealed class NullableDateTimeOffsetToUtcTicks() : ValueConverter<DateTimeOffset?, long?>(
        v => v == null ? null : v.Value.UtcTicks,
        v => v == null ? null : new DateTimeOffset(v.Value, TimeSpan.Zero));

    private sealed class TerminalIdConverter() : ValueConverter<TerminalId, string>(v => v.Value, v => TerminalId.Create(v));

    private sealed class MaskedPanConverter() : ValueConverter<MaskedPan, string>(v => v.Value, v => MaskedPan.Parse(v));

    private sealed class NullableCorrelationIdConverter() : ValueConverter<CorrelationId?, string?>(
        v => v == null ? null : v.Value.ToString(),
        v => v == null ? null : new CorrelationId(Guid.ParseExact(v, "N")));
}
