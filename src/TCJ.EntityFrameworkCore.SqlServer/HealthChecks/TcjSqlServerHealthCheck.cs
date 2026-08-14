using System.Data;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Core.Diagnostics;
using TCJ.Core.HealthChecks;
using TCJ.EntityFrameworkCore.Abstractions;

namespace TCJ.EntityFrameworkCore.SqlServer.HealthChecks;

internal sealed class TcjSqlServerHealthCheck<TDbContext> : IHealthCheck
    where TDbContext : DbContext, IReadDbContext, IWriteDbContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TcjHealthCheckOptions _options;
    private readonly TcjStartupDiagnostics _diagnostics;
    private readonly AsyncHealthCheckCache<HealthCheckResult> _cache;

    public TcjSqlServerHealthCheck(IServiceScopeFactory scopeFactory, TcjHealthCheckOptions options, TcjStartupDiagnostics diagnostics, TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _diagnostics = diagnostics;
        _cache = new AsyncHealthCheckCache<HealthCheckResult>(timeProvider, options.CacheDuration);
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => ExecuteAsync(cancellationToken);

    private async Task<HealthCheckResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        using Activity? activity = HealthCheckTelemetryDiagnostics.Start(TcjHealthCheckNames.Checks.SqlServer, TcjHealthCheckNames.Categories.SqlServer);
        try
        {
            HealthCheckResult result = await _cache.GetOrCreateAsync(CheckDatabaseAsync, cancellationToken).ConfigureAwait(false);
            HealthCheckTelemetryDiagnostics.Complete(
                activity,
                TcjHealthCheckNames.Checks.SqlServer,
                TcjHealthCheckNames.Categories.SqlServer,
                result.Status == HealthStatus.Healthy ? "healthy" : result.Status == HealthStatus.Degraded ? "degraded" : "unhealthy",
                Stopwatch.GetElapsedTime(started));
            return result;
        }
        catch (OperationCanceledException)
        {
            HealthCheckTelemetryDiagnostics.CompleteCanceled(
                activity,
                TcjHealthCheckNames.Checks.SqlServer,
                TcjHealthCheckNames.Categories.SqlServer,
                Stopwatch.GetElapsedTime(started));
            throw;
        }
    }

    private async Task<HealthCheckResult> CheckDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            TDbContext? dbContext = scope.ServiceProvider.GetService<TDbContext>();
            if (dbContext is null)
            {
                _diagnostics.Report("TCJ.SqlServer.MissingDbContext", $"TCJ.EntityFrameworkCore.SqlServer requires {typeof(TDbContext).Name} to be registered before SQL Server readiness can succeed.");
                return HealthCheckResult.Unhealthy("The configured SQL Server DbContext registration is unavailable.");
            }

            if (!string.Equals(dbContext.Database.ProviderName, TcjDiagnosticNames.Providers.SqlServer, StringComparison.Ordinal))
            {
                _diagnostics.Report("TCJ.SqlServer.InvalidProvider", "TCJ.EntityFrameworkCore.SqlServer requires a configured DbContext using the SQL Server provider.");
                return HealthCheckResult.Unhealthy("The configured DbContext is not using the SQL Server provider.");
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_options.DatabaseTimeout);
            var connection = dbContext.Database.GetDbConnection();
            bool openedHere = connection.State != ConnectionState.Open;
            try
            {
                if (openedHere)
                {
                    await connection.OpenAsync(timeoutSource.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                if (openedHere && connection.State == ConnectionState.Open)
                {
                    await connection.CloseAsync().ConfigureAwait(false);
                }
            }

            _diagnostics.Clear("TCJ.SqlServer.MissingDbContext");
            _diagnostics.Clear("TCJ.SqlServer.InvalidProvider");
            _diagnostics.Clear("TCJ.SqlServer.InvalidConfiguration");
            return HealthCheckResult.Healthy("SQL Server connectivity is available.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("SQL Server connectivity timed out.");
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
        {
            _diagnostics.Report(
                "TCJ.SqlServer.InvalidConfiguration",
                "TCJ.EntityFrameworkCore.SqlServer has invalid database connection or provider configuration. Verify the configured SQL Server DbContext without exposing connection details.");
            return HealthCheckResult.Unhealthy("SQL Server health-check configuration is invalid.");
        }
        catch
        {
            return HealthCheckResult.Unhealthy("SQL Server connectivity is unavailable.");
        }
    }
}
