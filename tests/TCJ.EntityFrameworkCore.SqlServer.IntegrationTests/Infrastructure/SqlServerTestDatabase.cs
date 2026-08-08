using Microsoft.Extensions.DependencyInjection;

namespace TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Infrastructure;

public sealed class SqlServerTestDatabase : IAsyncDisposable
{
    private readonly SqlServerContainerFixture _fixture;
    private bool _disposed;

    public SqlServerTestDatabase(
        SqlServerContainerFixture fixture,
        string databaseName,
        string connectionString,
        ServiceProvider services)
    {
        _fixture = fixture;
        DatabaseName = databaseName;
        ConnectionString = connectionString;
        Services = services;
    }

    public string DatabaseName { get; }

    public string ConnectionString { get; }

    public ServiceProvider Services { get; }

    public IServiceScope CreateScope() => Services.CreateScope();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await Services.DisposeAsync().ConfigureAwait(false);
        await _fixture.DropDatabaseAsync(DatabaseName).ConfigureAwait(false);
    }
}
