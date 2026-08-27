using Microsoft.EntityFrameworkCore;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;

namespace TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Infrastructure;

internal sealed class SqlServerTestDbContext(DbContextOptions<SqlServerTestDbContext> options)
    : DbContext(options), IReadDbContext, IWriteDbContext
{
    public DbSet<SqlServerTestEntity> TestEntities => Set<SqlServerTestEntity>();

    public DbSet<SqlServerParent> Parents => Set<SqlServerParent>();

    public DbSet<SqlServerChild> Children => Set<SqlServerChild>();

    public DbSet<SqlServerStrongIdRecord> StrongIdRecords => Set<SqlServerStrongIdRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        SqlServerTestDbContextModelBuilder.Build(modelBuilder);
    }

}
