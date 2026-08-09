using Microsoft.EntityFrameworkCore;
using TCJ.Core.Entities;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;

namespace TCJ.Concurrency.Tests.Fixtures;

internal sealed class StressDbContext(DbContextOptions<StressDbContext> options) : DbContext(options), IReadDbContext, IWriteDbContext
{
    public DbSet<StressEntity> Entities => Set<StressEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StressEntity>(builder =>
        {
            builder.HasKey(entity => entity.Id);
            builder.Property(entity => entity.Name).HasMaxLength(128).IsRequired();
            builder.HasIndex(entity => entity.Name).IsUnique();
        });

        modelBuilder.Entity<StressRowVersionEntity>(builder =>
        {
            builder.HasKey(entity => entity.Id);
            builder.Property(entity => entity.Name).HasMaxLength(128).IsRequired();
        });

        modelBuilder.ApplyTcjSqlServerConventions();
    }
}

internal sealed class StressEntity : Entity<Guid>
{
    private StressEntity() { }
    public StressEntity(Guid id, string name) : base(id) => Name = name;
    public string Name { get; set; } = string.Empty;
}

internal sealed class StressRowVersionEntity : RowVersionAuditedEntity<Guid>
{
    private StressRowVersionEntity() { }
    public StressRowVersionEntity(Guid id, string name)
    {
        Id = id;
        Name = name;
    }
    public string Name { get; set; } = string.Empty;
}
