using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using TCJ.Core.Results;
using TCJ.Core.StrongTypes;
using TCJ.EntityFrameworkCore.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Infrastructure;
using TCJ.EntityFrameworkCore.StrongTypes;

namespace TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Tests;

[Collection(SqlServerIntegrationCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Category", "SqlServer")]
[Trait("Category", "Database")]
public sealed class ValueObjectConversionIntegrationTests(SqlServerContainerFixture fixture)
    : SqlServerIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Supported_value_objects_round_trip_as_primitives_and_track_updates_by_value()
    {
        await using var context = CreateContext();
        await CreateValueObjectTableAsync(context);

        Guid guid = Guid.Parse("7d057036-3cdb-4329-aa7a-5ef9f6360357");
        SqlServerCode code = SqlServerCode.Create(" abc ").Value;
        SqlServerGuidValue guidValue = SqlServerGuidValue.Create(guid).Value;
        SqlServerIntValue intValue = SqlServerIntValue.Create(42).Value;
        SqlServerLongValue longValue = SqlServerLongValue.Create(9_000_000_000L).Value;
        SqlServerDecimalValue decimalValue = SqlServerDecimalValue.Create(1250.50m).Value;

        context.ValueObjectRecords.Add(new SqlServerValueObjectRecord(
            1,
            code,
            guidValue,
            intValue,
            longValue,
            decimalValue));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        SqlServerValueObjectRecord stored = await context.ValueObjectRecords.SingleAsync(value =>
            value.Code == code
            && value.GuidValue == guidValue
            && value.IntValue == intValue
            && value.LongValue == longValue
            && value.DecimalValue == decimalValue);

        Assert.Equal("ABC", stored.Code.Value);
        Assert.Equal(guid, stored.GuidValue.Value);
        Assert.Equal(42, stored.IntValue.Value);
        Assert.Equal(9_000_000_000L, stored.LongValue.Value);
        Assert.Equal(1250.50m, stored.DecimalValue.Value);

        EntityEntry entry = context.Entry(stored);
        stored.Code = SqlServerCode.Create(" ABC ").Value;
        context.ChangeTracker.DetectChanges();
        Assert.False(entry.Property(nameof(SqlServerValueObjectRecord.Code)).IsModified);

        stored.Code = SqlServerCode.Create("xyz").Value;
        context.ChangeTracker.DetectChanges();
        Assert.True(entry.Property(nameof(SqlServerValueObjectRecord.Code)).IsModified);
        await context.SaveChangesAsync();

        await context.Database.OpenConnectionAsync();
        try
        {
            await using DbCommand command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText =
                "SELECT [Code], [GuidValue], [IntValue], [LongValue], [DecimalValue] FROM [ValueObjectRecords] WHERE [Id] = 1";

            await using DbDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("XYZ", reader.GetString(0));
            Assert.Equal(guid, reader.GetGuid(1));
            Assert.Equal(42, reader.GetInt32(2));
            Assert.Equal(9_000_000_000L, reader.GetInt64(3));
            Assert.Equal(1250.50m, reader.GetDecimal(4));
            Assert.False(await reader.ReadAsync());
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task Invalid_legacy_scalar_fails_materialization_without_exposing_sensitive_data()
    {
        const string rejectedValue = "secret-invalid-legacy-value";

        await using var context = CreateContext();
        await CreateValueObjectTableAsync(context);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO [ValueObjectRecords] ([Id], [Code], [GuidValue], [IntValue], [LongValue], [DecimalValue])
            VALUES (2, {rejectedValue}, '7d057036-3cdb-4329-aa7a-5ef9f6360357', 42, 84, 12.5000);
            """);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await context.ValueObjectRecords.SingleAsync(value => value.Id == 2));

        Assert.Contains(typeof(SqlServerCode).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Review or migrate the stored database value", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(rejectedValue, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Rejected legacy value", exception.Message, StringComparison.Ordinal);
    }

    private ValueObjectSqlServerDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ValueObjectSqlServerDbContext>()
            .UseSqlServer(
                Database.ConnectionString,
                sql => sql.CommandTimeout(Fixture.Policy.CommandTimeoutSeconds))
            .Options;
        return new ValueObjectSqlServerDbContext(options);
    }

    private static async Task CreateValueObjectTableAsync(ValueObjectSqlServerDbContext context)
    {
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE [ValueObjectRecords]
            (
                [Id] int NOT NULL CONSTRAINT [PK_ValueObjectRecords] PRIMARY KEY,
                [Code] nvarchar(64) NOT NULL,
                [GuidValue] uniqueidentifier NOT NULL,
                [IntValue] int NOT NULL,
                [LongValue] bigint NOT NULL,
                [DecimalValue] decimal(18,4) NOT NULL
            );
            """);
    }

    private sealed class ValueObjectSqlServerDbContext(DbContextOptions<ValueObjectSqlServerDbContext> options)
        : DbContext(options)
    {
        public DbSet<SqlServerValueObjectRecord> ValueObjectRecords => Set<SqlServerValueObjectRecord>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SqlServerValueObjectRecord>(entity =>
            {
                entity.ToTable("ValueObjectRecords");
                entity.HasKey(static value => value.Id);
                entity.Property(static value => value.Id).ValueGeneratedNever().HasColumnType("int");
                entity.Property(static value => value.Code).HasColumnType("nvarchar(64)").HasMaxLength(64);
                entity.Property(static value => value.GuidValue).HasColumnType("uniqueidentifier");
                entity.Property(static value => value.IntValue).HasColumnType("int");
                entity.Property(static value => value.LongValue).HasColumnType("bigint");
                entity.Property(static value => value.DecimalValue).HasColumnType("decimal(18,4)").HasPrecision(18, 4);
            });

            var valueObjects = new ValueObjectConversionRegistry()
                .Register<SqlServerCode, string>(
                    SqlServerCode.ValueObjectConversion.ToBackingValue,
                    SqlServerCode.ValueObjectConversion.FromBackingValue)
                .Register<SqlServerGuidValue, Guid>(
                    SqlServerGuidValue.ValueObjectConversion.ToBackingValue,
                    SqlServerGuidValue.ValueObjectConversion.FromBackingValue)
                .Register<SqlServerIntValue, int>(
                    SqlServerIntValue.ValueObjectConversion.ToBackingValue,
                    SqlServerIntValue.ValueObjectConversion.FromBackingValue)
                .Register<SqlServerLongValue, long>(
                    SqlServerLongValue.ValueObjectConversion.ToBackingValue,
                    SqlServerLongValue.ValueObjectConversion.FromBackingValue)
                .Register<SqlServerDecimalValue, decimal>(
                    SqlServerDecimalValue.ValueObjectConversion.ToBackingValue,
                    SqlServerDecimalValue.ValueObjectConversion.FromBackingValue);

            modelBuilder.ApplyValueObjectConversions(valueObjects);
        }
    }
}

[ValueObject<string>]
internal readonly partial record struct SqlServerCode
{
    private static string Normalize(string value) => value.Trim().ToUpperInvariant();

    private static Result Validate(string value)
        => value.Length == 3
            ? Result.Success()
            : Result.Failure(new ResultError("code.invalid", $"Rejected legacy value '{value}'."));
}

[ValueObject<Guid>]
internal readonly partial record struct SqlServerGuidValue
{
    private static Result Validate(Guid value) => value != Guid.Empty
        ? Result.Success()
        : Result.Failure(new ResultError("guid.empty", "GUID must not be empty."));
}

[ValueObject<int>]
internal readonly partial record struct SqlServerIntValue
{
    private static Result Validate(int value) => value >= 0
        ? Result.Success()
        : Result.Failure(new ResultError("int.negative", "Value must be non-negative."));
}

[ValueObject<long>]
internal readonly partial record struct SqlServerLongValue
{
    private static Result Validate(long value) => value >= 0
        ? Result.Success()
        : Result.Failure(new ResultError("long.negative", "Value must be non-negative."));
}

[ValueObject<decimal>]
internal readonly partial record struct SqlServerDecimalValue
{
    private static Result Validate(decimal value) => value >= 0m
        ? Result.Success()
        : Result.Failure(new ResultError("decimal.negative", "Value must be non-negative."));
}

internal sealed class SqlServerValueObjectRecord
{
    private SqlServerValueObjectRecord()
    {
    }

    public SqlServerValueObjectRecord(
        int id,
        SqlServerCode code,
        SqlServerGuidValue guidValue,
        SqlServerIntValue intValue,
        SqlServerLongValue longValue,
        SqlServerDecimalValue decimalValue)
    {
        Id = id;
        Code = code;
        GuidValue = guidValue;
        IntValue = intValue;
        LongValue = longValue;
        DecimalValue = decimalValue;
    }

    public int Id { get; private set; }

    public SqlServerCode Code { get; set; }

    public SqlServerGuidValue GuidValue { get; private set; }

    public SqlServerIntValue IntValue { get; private set; }

    public SqlServerLongValue LongValue { get; private set; }

    public SqlServerDecimalValue DecimalValue { get; private set; }
}
