using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Core.Diagnostics;
using TCJ.Core.HealthChecks;
using TCJ.EntityFrameworkCore.Abstractions;

namespace TCJ.EntityFrameworkCore.SqlServer.HealthChecks;

internal sealed class TcjSqlServerMigrationHealthCheck<TDbContext> : IHealthCheck
    where TDbContext : DbContext, IReadDbContext, IWriteDbContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TcjHealthCheckOptions _options;
    private readonly AsyncHealthCheckCache<HealthCheckResult> _cache;

    public TcjSqlServerMigrationHealthCheck(IServiceScopeFactory scopeFactory, TcjHealthCheckOptions options, TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _cache = new AsyncHealthCheckCache<HealthCheckResult>(timeProvider, options.CacheDuration);
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => ExecuteAsync(cancellationToken);

    private async Task<HealthCheckResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        using Activity? activity = HealthCheckTelemetryDiagnostics.Start(TcjHealthCheckNames.Checks.SqlServerMigrations, TcjHealthCheckNames.Categories.Configuration);
        try
        {
            HealthCheckResult result = await _cache.GetOrCreateAsync(CheckMigrationsAsync, cancellationToken).ConfigureAwait(false);
            string status = result.Status == HealthStatus.Healthy ? "healthy" : result.Status == HealthStatus.Degraded ? "degraded" : "unhealthy";
            HealthCheckTelemetryDiagnostics.Complete(
                activity,
                TcjHealthCheckNames.Checks.SqlServerMigrations,
                TcjHealthCheckNames.Categories.Configuration,
                status,
                Stopwatch.GetElapsedTime(started));
            return result;
        }
        catch (OperationCanceledException)
        {
            HealthCheckTelemetryDiagnostics.CompleteCanceled(
                activity,
                TcjHealthCheckNames.Checks.SqlServerMigrations,
                TcjHealthCheckNames.Categories.Configuration,
                Stopwatch.GetElapsedTime(started));
            throw;
        }
    }

    private async Task<HealthCheckResult> CheckMigrationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            TDbContext? dbContext = scope.ServiceProvider.GetService<TDbContext>();
            if (dbContext is null || !string.Equals(dbContext.Database.ProviderName, TcjDiagnosticNames.Providers.SqlServer, StringComparison.Ordinal))
            {
                return HealthCheckResult.Unhealthy("SQL Server migration readiness is not correctly configured.");
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_options.DatabaseTimeout);
            string[] pending = (await dbContext.Database.GetPendingMigrationsAsync(timeoutSource.Token).ConfigureAwait(false)).Take(1).ToArray();
            if (pending.Length == 0)
            {
                return HealthCheckResult.Healthy("No pending SQL Server migrations were detected.");
            }

            HealthCheckResult result = _options.PendingMigrationsStatus == TcjPendingMigrationsStatus.Unhealthy
                ? HealthCheckResult.Unhealthy("Pending SQL Server migrations were detected.")
                : HealthCheckResult.Degraded("Pending SQL Server migrations were detected.");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("SQL Server migration readiness timed out.");
        }
        catch
        {
            return HealthCheckResult.Unhealthy("SQL Server migration readiness could not be evaluated.");
        }
    }
}
