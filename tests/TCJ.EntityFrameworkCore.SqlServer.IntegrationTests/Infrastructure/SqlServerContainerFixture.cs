using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.Security;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;
using Testcontainers.MsSql;

namespace TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Infrastructure;

public sealed class SqlServerContainerFixture : IAsyncLifetime
{
    private const long TestUserId = 7001;
    private readonly SqlServerIntegrationPolicy _policy = SqlServerIntegrationPolicy.Load();
    private readonly string _password = CreatePassword();
    private readonly Stopwatch _startupStopwatch = new();
    private readonly string _diagnosticsDirectory;
    private MsSqlContainer? _container;
    private bool _ready;
    private int _migratedDatabaseCount;

    public SqlServerContainerFixture()
    {
        string root = RepositoryPaths.FindRoot();
        string configuredResults = Environment.GetEnvironmentVariable("TCJ_SQLSERVER_RESULTS_DIR") ?? string.Empty;
        string resultsDirectory = string.IsNullOrWhiteSpace(configuredResults)
            ? Path.Combine(root, "TestResults", "SqlServerIntegration")
            : Path.GetFullPath(configuredResults, root);
        _diagnosticsDirectory = Path.Combine(resultsDirectory, "diagnostics");
    }

    internal SqlServerIntegrationPolicy Policy => _policy;

    internal string ContainerConnectionString =>
        _container?.GetConnectionString()
        ?? throw new InvalidOperationException("The SQL Server container has not started.");

    internal bool IsReady => _ready;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_diagnosticsDirectory);

        _container = new MsSqlBuilder(_policy.ContainerImage)
            .WithPassword(_password)
            .WithLabel("tcj.sqlserver.integration", "true")
            .WithCleanUp(true)
            .Build();

        using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(_policy.StartupTimeoutSeconds));
        _startupStopwatch.Start();

        try
        {
            await _container.StartAsync(startupTimeout.Token).ConfigureAwait(false);
            await WaitUntilReadyAsync(startupTimeout.Token).ConfigureAwait(false);
            _ready = true;
        }
        catch (Exception exception)
        {
            await WriteFailureDiagnosticsAsync("container-startup", exception).ConfigureAwait(false);
            await DisposeContainerAfterFailureAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"SQL Server Testcontainer did not become ready within {_policy.StartupTimeoutSeconds} seconds. " +
                $"Sanitized diagnostics are available under '{_diagnosticsDirectory}'.");
        }
        finally
        {
            _startupStopwatch.Stop();
            await WriteRuntimeSummaryAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await WriteContainerLogsAsync().ConfigureAwait(false);
            try
            {
                await _container.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await WriteFailureDiagnosticsAsync("container-cleanup", exception).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"SQL Server Testcontainer cleanup failed. Sanitized diagnostics are available under '{_diagnosticsDirectory}'.");
            }
            finally
            {
                _container = null;
            }
        }

        await WriteRuntimeSummaryAsync().ConfigureAwait(false);
    }

    internal async Task<SqlServerTestDatabase> CreateDatabaseAsync()
    {
        EnsureReady();

        string databaseName = $"TCJ_Integration_{Guid.NewGuid():N}";
        await ExecuteMasterCommandAsync($"CREATE DATABASE [{databaseName}]").ConfigureAwait(false);

        string connectionString = BuildConnectionString(databaseName);
        ServiceProvider services = BuildServices(connectionString);

        try
        {
            using IServiceScope scope = services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<SqlServerTestDbContext>();
            if (context.Database.HasPendingModelChanges())
            {
                await WriteMigrationModelDiagnosticsAsync(context).ConfigureAwait(false);
            }

            await context.Database.MigrateAsync().ConfigureAwait(false);
            Interlocked.Increment(ref _migratedDatabaseCount);
            await WriteRuntimeSummaryAsync().ConfigureAwait(false);
            return new SqlServerTestDatabase(this, databaseName, connectionString, services);
        }
        catch (Exception exception)
        {
            await services.DisposeAsync().ConfigureAwait(false);
            await WriteFailureDiagnosticsAsync("migration", exception).ConfigureAwait(false);
            try
            {
                await DropDatabaseAsync(databaseName).ConfigureAwait(false);
            }
            catch
            {
                // The original migration failure remains the primary failure. Cleanup diagnostics are already sanitized.
            }

            throw new InvalidOperationException(
                $"SQL Server migration failed for isolated database '{databaseName}'. " +
                $"Sanitized diagnostics are available under '{_diagnosticsDirectory}'.");
        }
    }

    internal async Task DropDatabaseAsync(string databaseName)
    {
        if (!_ready)
        {
            return;
        }

        string command = $"IF DB_ID(N'{databaseName}') IS NOT NULL BEGIN " +
                         $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                         $"DROP DATABASE [{databaseName}]; END";

        try
        {
            await ExecuteMasterCommandAsync(command).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await WriteFailureDiagnosticsAsync("database-cleanup", exception).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Failed to clean up isolated SQL Server database '{databaseName}'. " +
                $"Sanitized diagnostics are available under '{_diagnosticsDirectory}'.");
        }
    }

    internal string BuildConnectionString(string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(ContainerConnectionString)
        {
            InitialCatalog = databaseName,
            TrustServerCertificate = true
        };
        return builder.ConnectionString;
    }

    private ServiceProvider BuildServices(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserProvider>(new TestCurrentUserProvider(TestUserId));
        services.AddTcjSqlServer<SqlServerTestDbContext>(
            connectionString,
            options =>
            {
                options.EnableRetryOnFailure = false;
                options.CommandTimeout = _policy.CommandTimeoutSeconds;
                options.MigrationsAssembly = typeof(SqlServerTestDbContext).Assembly.GetName().Name;
            });

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        string masterConnection = BuildConnectionString("master");
        var options = new DbContextOptionsBuilder<DbContext>()
            .UseSqlServer(masterConnection, sql => sql.CommandTimeout(_policy.CommandTimeoutSeconds))
            .Options;

        await using var context = new DbContext(options);
        bool canConnect = await context.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);

        if (!canConnect)
        {
            throw new InvalidOperationException("SQL Server readiness probe could not open a database connection.");
        }
    }

    private async Task ExecuteMasterCommandAsync(string command)
    {
        var options = new DbContextOptionsBuilder<DbContext>()
            .UseSqlServer(BuildConnectionString("master"), sql => sql.CommandTimeout(_policy.CommandTimeoutSeconds))
            .Options;

        await using var context = new DbContext(options);
        await context.Database.ExecuteSqlRawAsync(command).ConfigureAwait(false);
    }

    private void EnsureReady()
    {
        if (!_ready || _container is null)
        {
            throw new InvalidOperationException("The SQL Server integration fixture is not ready.");
        }
    }


    private async Task DisposeContainerAfterFailureAsync()
    {
        if (_container is null)
        {
            return;
        }

        try
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await File.WriteAllTextAsync(
                Path.Combine(_diagnosticsDirectory, "container-cleanup.log"),
                Sanitize(exception.ToString())).ConfigureAwait(false);
        }
        finally
        {
            _container = null;
        }
    }

    private async Task WriteContainerLogsAsync()
    {
        if (_container is null)
        {
            return;
        }

        try
        {
            var (stdout, stderr) = await _container.GetLogsAsync().ConfigureAwait(false);
            string content = $"STDOUT{Environment.NewLine}{stdout}{Environment.NewLine}STDERR{Environment.NewLine}{stderr}";
            await File.WriteAllTextAsync(
                Path.Combine(_diagnosticsDirectory, "container.log"),
                Sanitize(content)).ConfigureAwait(false);
            await WriteSqlServerErrorLogAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await File.WriteAllTextAsync(
                Path.Combine(_diagnosticsDirectory, "container-log-collection-error.log"),
                Sanitize(exception.ToString())).ConfigureAwait(false);
        }
    }

    private async Task WriteSqlServerErrorLogAsync()
    {
        if (_container is null)
        {
            return;
        }

        try
        {
            byte[] errorLog = await _container.ReadFileAsync("/var/opt/mssql/log/errorlog").ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(_diagnosticsDirectory, "sqlserver-error.log"),
                Sanitize(Encoding.UTF8.GetString(errorLog))).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await File.WriteAllTextAsync(
                Path.Combine(_diagnosticsDirectory, "sqlserver-error-log-collection-error.log"),
                Sanitize(exception.ToString())).ConfigureAwait(false);
        }
    }

    private async Task WriteMigrationModelDiagnosticsAsync(SqlServerTestDbContext context)
    {
        try
        {
            IDesignTimeModel designTimeModel = context.GetService<IDesignTimeModel>();
            IMigrationsAssembly migrationsAssembly = context.GetService<IMigrationsAssembly>();
            string currentModel = designTimeModel.Model.ToDebugString(MetadataDebugStringOptions.LongDefault);
            string snapshotModel = migrationsAssembly.ModelSnapshot?.Model.ToDebugString(MetadataDebugStringOptions.LongDefault)
                                   ?? "<missing migration snapshot>";

            await File.WriteAllTextAsync(
                Path.Combine(_diagnosticsDirectory, "migration-current-model.log"),
                Sanitize(currentModel)).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(_diagnosticsDirectory, "migration-snapshot-model.log"),
                Sanitize(snapshotModel)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await File.WriteAllTextAsync(
                Path.Combine(_diagnosticsDirectory, "migration-model-diagnostics-error.log"),
                Sanitize(exception.ToString())).ConfigureAwait(false);
        }
    }

    private async Task WriteFailureDiagnosticsAsync(string name, Exception exception)
    {
        Directory.CreateDirectory(_diagnosticsDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(_diagnosticsDirectory, $"{name}.log"),
            Sanitize(exception.ToString())).ConfigureAwait(false);
        await WriteContainerLogsAsync().ConfigureAwait(false);
    }

    private async Task WriteRuntimeSummaryAsync()
    {
        Directory.CreateDirectory(_diagnosticsDirectory);
        var summary = new
        {
            containerImage = _policy.ContainerImage,
            startupDurationSeconds = Math.Round(_startupStopwatch.Elapsed.TotalSeconds, 3),
            readinessProbe = _ready ? "passed" : "not-passed",
            migratedDatabaseCount = Volatile.Read(ref _migratedDatabaseCount),
            databaseIsolation = _policy.DatabaseIsolation
        };

        string json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(
            Path.Combine(_diagnosticsDirectory, "runtime-summary.json"),
            json).ConfigureAwait(false);
    }

    private string Sanitize(string value)
    {
        string sanitized = value.Replace(_password, "<redacted>", StringComparison.Ordinal);
        sanitized = Regex.Replace(
            sanitized,
            @"(?i)(Password|Pwd)\s*=\s*[^;\r\n]+",
            "$1=<redacted>",
            RegexOptions.CultureInvariant);
        return sanitized;
    }

    private static string CreatePassword()
    {
        string random = Convert.ToHexString(RandomNumberGenerator.GetBytes(18));
        return $"Tcj!aA1_{random}";
    }
}
