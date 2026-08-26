using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Infrastructure;

namespace TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Tests;

[Collection(SqlServerIntegrationCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Category", "SqlServer")]
[Trait("Category", "Database")]
public sealed class StrongIdConversionIntegrationTests(SqlServerContainerFixture fixture)
    : SqlServerIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Guid_int_and_long_strong_ids_translate_and_round_trip_as_primitive_values()
    {
        Guid guidValue = Guid.Parse("86b141d4-adbc-4b5a-a3a8-5346dc8fb264");
        const int intValue = -42;
        const long longValue = 9_223_372_036_854_775_000;
        var guidId = new SqlServerStrongGuidId(guidValue);
        var intId = new SqlServerStrongIntId(intValue);
        var longId = new SqlServerStrongLongId(longValue);

        using IServiceScope scope = Database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SqlServerTestDbContext>();
        context.StrongIdRecords.Add(new SqlServerStrongIdRecord(guidId, intId, longId));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        SqlServerStrongIdRecord stored = await context.StrongIdRecords.SingleAsync(value =>
            value.Id == guidId
            && value.IntId == intId
            && value.LongId == longId);

        Assert.Equal(guidId, stored.Id);
        Assert.Equal(intId, stored.IntId);
        Assert.Equal(longId, stored.LongId);
        Assert.Null(stored.OptionalGuidId);

        await context.Database.OpenConnectionAsync();
        try
        {
            await using DbCommand command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText =
                "SELECT [Id], [IntId], [LongId], [OptionalGuidId] FROM [StrongIdRecords] WHERE [Id] = @id";
            DbParameter idParameter = command.CreateParameter();
            idParameter.ParameterName = "@id";
            idParameter.Value = guidValue;
            command.Parameters.Add(idParameter);

            await using DbDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(guidValue, reader.GetGuid(0));
            Assert.Equal(intValue, reader.GetInt32(1));
            Assert.Equal(longValue, reader.GetInt64(2));
            Assert.True(reader.IsDBNull(3));
            Assert.False(await reader.ReadAsync());
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}
