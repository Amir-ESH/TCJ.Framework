using Microsoft.EntityFrameworkCore;
using TCJ.Core.Entities;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;

namespace TcjEfNativeAot;

public static class Program
{
    public static void Main()
    {
        using var dbContext = new ExperimentalNativeAotDbContext();
        _ = dbContext.Model;

        // Keep a representative statically analyzable LINQ query in the fixture so
        // EF's publish-time query precompiler has an application query to discover.
        _ = (Func<ExperimentalNativeAotDbContext, Task<List<string>>>)LoadNamesAsync;

        Console.WriteLine("TCJ EF Core NativeAOT experimental fixture initialized");
    }

    private static Task<List<string>> LoadNamesAsync(ExperimentalNativeAotDbContext dbContext)
        => dbContext.Records
            .Where(static record => record.Name.StartsWith("A"))
            .OrderBy(static record => record.Name)
            .Select(static record => record.Name)
            .ToListAsync();
}

public sealed class ExperimentalNativeAotDbContext : DbContext, IReadDbContext, IWriteDbContext
{
    private const string ConnectionString =
        "Server=127.0.0.1,1433;Database=TcjNativeAotExperimental;Integrated Security=True;Encrypt=False;TrustServerCertificate=True";

    public DbSet<ExperimentalRecord> Records => Set<ExperimentalRecord>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.UseSqlServer(ConnectionString);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<ExperimentalRecord>(builder =>
        {
            builder.HasKey(static record => record.Id);
            builder.Property(static record => record.Name).HasMaxLength(128).IsRequired();
        });
        modelBuilder.ApplyTcjSqlServerConventions();
    }
}

public sealed class ExperimentalRecord : RowVersionAuditedEntity<Guid>
{
    public string Name { get; set; } = string.Empty;
}
