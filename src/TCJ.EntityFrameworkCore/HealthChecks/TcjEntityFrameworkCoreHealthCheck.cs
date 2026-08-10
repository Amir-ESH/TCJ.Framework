using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Core.Diagnostics;
using TCJ.Core.HealthChecks;
using TCJ.EntityFrameworkCore.Abstractions;

namespace TCJ.EntityFrameworkCore.HealthChecks;

internal sealed class TcjEntityFrameworkCoreHealthCheck<TDbContext>(IServiceScopeFactory scopeFactory, TcjStartupDiagnostics diagnostics) : IHealthCheck
    where TDbContext : DbContext, IReadDbContext, IWriteDbContext
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();
        using Activity? activity = HealthCheckTelemetryDiagnostics.Start(TcjHealthCheckNames.Checks.EntityFrameworkCore, TcjHealthCheckNames.Categories.Database);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            TDbContext? dbContext = scope.ServiceProvider.GetService<TDbContext>();
            HealthCheckResult result;
            if (dbContext is null)
            {
                diagnostics.Report("TCJ.EF.MissingDbContext", $"TCJ.EntityFrameworkCore requires {typeof(TDbContext).Name} to be registered before database readiness can succeed.");
                result = HealthCheckResult.Unhealthy("The configured TCJ DbContext registration is unavailable.");
            }
            else if (string.IsNullOrWhiteSpace(dbContext.Database.ProviderName))
            {
                diagnostics.Report("TCJ.EF.MissingProvider", "TCJ.EntityFrameworkCore requires a configured database provider.");
                result = HealthCheckResult.Unhealthy("The configured TCJ DbContext has no database provider.");
            }
            else
            {
                _ = dbContext.Model.GetEntityTypes().Count();
                diagnostics.Clear("TCJ.EF.MissingDbContext");
                diagnostics.Clear("TCJ.EF.MissingProvider");
                result = HealthCheckResult.Healthy("TCJ Entity Framework Core model and provider configuration are available.");
            }

            Complete(result);
            return Task.FromResult(result);
        }
        catch (OperationCanceledException)
        {
            HealthCheckTelemetryDiagnostics.CompleteCanceled(activity, TcjHealthCheckNames.Checks.EntityFrameworkCore, TcjHealthCheckNames.Categories.Database, Stopwatch.GetElapsedTime(started));
            throw;
        }
        catch
        {
            diagnostics.Report("TCJ.EF.InitializationFailure", "TCJ.EntityFrameworkCore DbContext model initialization failed. Verify the DbContext registration and provider configuration.");
            HealthCheckResult result = HealthCheckResult.Unhealthy("TCJ Entity Framework Core initialization failed.");
            Complete(result);
            return Task.FromResult(result);
        }

        void Complete(HealthCheckResult result) => HealthCheckTelemetryDiagnostics.Complete(
            activity,
            TcjHealthCheckNames.Checks.EntityFrameworkCore,
            TcjHealthCheckNames.Categories.Database,
            result.Status == HealthStatus.Healthy ? "healthy" : result.Status == HealthStatus.Degraded ? "degraded" : "unhealthy",
            Stopwatch.GetElapsedTime(started));
    }
}
