using Microsoft.EntityFrameworkCore;
using TCJ.EntityFrameworkCore.Abstractions;

namespace TCJ.HealthChecks.Tests.Infrastructure;

internal sealed class HealthTestDbContext(DbContextOptions<HealthTestDbContext> options)
    : DbContext(options), IReadDbContext, IWriteDbContext
{
    internal DbSet<HealthTestRow> Rows => Set<HealthTestRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HealthTestRow>(builder =>
        {
            builder.HasKey(row => row.Id);
            builder.Property(row => row.Value).HasMaxLength(100).IsRequired();
        });
    }
}

internal sealed class HealthTestRow
{
    public int Id { get; set; }
    public string Value { get; set; } = string.Empty;
}
