using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.Diagnostics;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;

namespace TCJ.Observability.Tests;

public sealed class SqlServerTelemetryTests : IDisposable
{
    private const string PasswordMarker = "TCJ_TEST_PASSWORD_MARKER";
    private const string ConnectionStringMarker = "TCJ_TEST_CONNECTION_STRING_MARKER";

    public SqlServerTelemetryTests() => TcjTelemetry.ResetForTests();

    [Fact]
    public void Provider_configuration_records_provider_but_never_connection_string_values()
    {
        using var collector = new ActivityCollector(TcjDiagnosticNames.Sources.EntityFrameworkCoreSqlServer);
        var services = new ServiceCollection();
        services.AddTcjSqlServer<TestSqlDbContext>(
            $"Server={ConnectionStringMarker};Database=tcj;User Id=sa;Password={PasswordMarker};TrustServerCertificate=true");
        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        _ = scope.ServiceProvider.GetRequiredService<TestSqlDbContext>();

        Activity activity = Assert.Single(
            collector.Activities,
            item => item.OperationName == TcjDiagnosticNames.Activities.SqlServerConfigure);
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
        Assert.Equal(
            TcjDiagnosticNames.Providers.SqlServer,
            activity.TagObjects.First(tag => tag.Key == TcjDiagnosticNames.Tags.DatabaseProvider).Value);

        string emitted = string.Join(
            '\n',
            activity.TagObjects.Select(static tag => $"{tag.Key}={tag.Value}"));
        Assert.DoesNotContain(PasswordMarker, emitted, StringComparison.Ordinal);
        Assert.DoesNotContain(ConnectionStringMarker, emitted, StringComparison.Ordinal);
        Assert.DoesNotContain("connection_string", emitted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw_sql", emitted, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => TcjTelemetry.ResetForTests();

    public sealed class TestSqlDbContext : DbContext, IReadDbContext, IWriteDbContext
    {
        public TestSqlDbContext(DbContextOptions<TestSqlDbContext> options)
            : base(options)
        {
        }
    }
}
