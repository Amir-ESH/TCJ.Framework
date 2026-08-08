namespace TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Infrastructure;

public abstract class SqlServerIntegrationTestBase(SqlServerContainerFixture fixture) : IAsyncLifetime
{
    protected SqlServerContainerFixture Fixture { get; } = fixture;

    protected SqlServerTestDatabase Database { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Database = await Fixture.CreateDatabaseAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Database is not null)
        {
            await Database.DisposeAsync().ConfigureAwait(false);
        }
    }
}
