namespace TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Infrastructure;

[CollectionDefinition("SQL Server integration")]
public sealed class SqlServerIntegrationCollection : ICollectionFixture<SqlServerContainerFixture>
{
    public const string Name = "SQL Server integration";
}
