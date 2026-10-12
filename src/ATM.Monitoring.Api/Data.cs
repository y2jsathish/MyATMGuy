using Microsoft.EntityFrameworkCore;

namespace ATM.Monitoring.Api;

public sealed class MonitoringApiOptions
{
    public const string SectionName = "Monitoring";

    public string Provider { get; set; } = "Sqlite";

    public string ConnectionString { get; set; } = "Data Source=monitoring.db";

    public string ApiKey { get; set; } = string.Empty;
}

public sealed class TerminalRow
{
    public string TerminalId { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Machine { get; set; } = string.Empty;

    public string Environment { get; set; } = string.Empty;

    public DateTimeOffset LastHeartbeatUtc { get; set; }

    public string? Overall { get; set; }

    public string? HealthJson { get; set; }

    public int ErrorTotal { get; set; }
}

public sealed class ErrorRow
{
    public long Id { get; set; }

    public string TerminalId { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public string Severity { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public Guid? CorrelationId { get; set; }

    public DateTimeOffset OccurredUtc { get; set; }
}

public sealed class MonitoringDbContext(DbContextOptions<MonitoringDbContext> options) : DbContext(options)
{
    public DbSet<TerminalRow> Terminals => Set<TerminalRow>();

    public DbSet<ErrorRow> Errors => Set<ErrorRow>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot order or compare DateTimeOffset; store UTC ticks on every provider for identical behaviour.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToTicks>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TerminalRow>(b =>
        {
            b.ToTable("Terminals");
            b.HasKey(x => x.TerminalId);
            b.Property(x => x.TerminalId).HasMaxLength(32);
            b.Property(x => x.Version).HasMaxLength(32);
            b.Property(x => x.Machine).HasMaxLength(128);
            b.Property(x => x.Environment).HasMaxLength(16);
            b.Property(x => x.Overall).HasMaxLength(16);
        });

        modelBuilder.Entity<ErrorRow>(b =>
        {
            b.ToTable("Errors");
            b.HasKey(x => x.Id);
            b.Property(x => x.TerminalId).HasMaxLength(32);
            b.Property(x => x.Code).HasMaxLength(16);
            b.Property(x => x.Severity).HasMaxLength(16);
            b.Property(x => x.Message).HasMaxLength(2048);
            b.HasIndex(x => new { x.TerminalId, x.OccurredUtc });
        });
    }

    private sealed class DateTimeOffsetToTicks() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(
        v => v.UtcTicks,
        v => new DateTimeOffset(v, TimeSpan.Zero));
}

/// <summary>Lets a test app start the API in-process.</summary>
public static class ProgramEntry
{
    public static void Run(string[] args) => typeof(global::Program).Assembly.EntryPoint!.Invoke(null, [args]);
}
