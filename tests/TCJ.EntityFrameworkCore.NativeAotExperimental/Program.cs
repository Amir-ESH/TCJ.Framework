using Microsoft.EntityFrameworkCore;
using TCJ.Core.Entities;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Extensions;
using TCJ.EntityFrameworkCore.StrongTypes;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;

namespace TcjEfNativeAot;

public static class Program
{
    public static async Task Main(string[] args)
    {
        using var dbContext = new ExperimentalNativeAotDbContext();
        _ = dbContext.Model;

        // EF's query precompiler recognizes this query because its root DbContext is a local
        // variable. Execution is opt-in so the published smoke binary does not require a live
        // SQL Server merely to prove compiled-model / precompiled-query Native AOT startup.
        if (args.Contains("--execute-query", StringComparer.Ordinal))
        {
            _ = await dbContext.Records
                .Where(record => record.Name.StartsWith('A'))
                .OrderBy(record => record.Name)
                .Select(record => record.Name)
                .ToListAsync();
        }

        Console.WriteLine("TCJ EF Core NativeAOT experimental fixture initialized");
    }
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
            builder.Property(static record => record.Id).ValueGeneratedNever();
            builder.Property(static record => record.Name).HasMaxLength(128).IsRequired();
        });

        var strongIds = new StrongIdConversionRegistry()
            .Register<ExperimentalRecordId, Guid>(
                ExperimentalRecordId.StrongIdConversion.ToBackingValue,
                ExperimentalRecordId.StrongIdConversion.FromBackingValue);
        modelBuilder.ApplyStrongIdConversions(strongIds);
        modelBuilder.ApplyTcjSqlServerConventions();
    }
}

public sealed class ExperimentalRecord : RowVersionAuditedEntity<ExperimentalRecordId>
{
    public string Name { get; set; } = string.Empty;
}

// This fixture mirrors the provider-neutral conversion surface emitted by TCJ.Generators.
// EF Core query precompilation recompiles startup sources in a secondary MSBuildWorkspace,
// where analyzer-generated members are not reliably available. Generator output itself is
// covered by TCJ.Generators.Tests and the SQL Server integration tests use real generated IDs.
public readonly record struct ExperimentalRecordId(Guid Value)
{
    public static class StrongIdConversion
    {
        public static global::System.Linq.Expressions.Expression<global::System.Func<ExperimentalRecordId, Guid>> ToBackingValue { get; } =
            static value => value.Value;

        public static global::System.Linq.Expressions.Expression<global::System.Func<Guid, ExperimentalRecordId>> FromBackingValue { get; } =
            static value => new ExperimentalRecordId(value);
    }
}

