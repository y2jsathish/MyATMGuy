using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ATM.Infrastructure.Persistence;

/// <summary>Used only by "dotnet ef" to create migrations.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AtmDbContext>
{
    public AtmDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AtmDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
