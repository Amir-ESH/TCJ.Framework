using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.AspNetCore.Serialization;
using TCJ.Core.Diagnostics;
using TCJ.Core.HealthChecks;

namespace TCJ.AspNetCore.HealthChecks;

/// <summary>Writes sanitized JSON responses for TCJ health endpoints.</summary>
public static class TcjHealthResponseWriter
{
    private static readonly HashSet<string> StableCheckNames = new(StringComparer.Ordinal)
    {
        TcjHealthCheckNames.Checks.Core,
        TcjHealthCheckNames.Checks.Startup,
        TcjHealthCheckNames.Checks.DependencyInjection,
        TcjHealthCheckNames.Checks.DomainEvents,
        TcjHealthCheckNames.Checks.EntityFrameworkCore,
        TcjHealthCheckNames.Checks.SqlServer,
        TcjHealthCheckNames.Checks.SqlServerMigrations
    };
    private static readonly HashSet<string> StableTags = new(StringComparer.Ordinal)
    {
        TcjHealthCheckNames.Tags.Tcj,
        TcjHealthCheckNames.Tags.Live,
        TcjHealthCheckNames.Tags.Ready,
        TcjHealthCheckNames.Tags.Dependency,
        TcjHealthCheckNames.Tags.Startup,
        TcjHealthCheckNames.Tags.Database,
        TcjHealthCheckNames.Tags.SqlServer,
        TcjHealthCheckNames.Tags.Configuration
    };
    /// <summary>Writes the production-safe public response without individual check details.</summary>
    /// <param name="httpContext">Current HTTP context.</param>
    /// <param name="report">Completed health report.</param>
    /// <returns>A task that completes when the JSON response has been written.</returns>
    public static Task WritePublicAsync(HttpContext httpContext, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(report);
        Prepare(httpContext);
        return JsonSerializer.SerializeAsync(
            httpContext.Response.Body,
            new PublicHealthResponse(report.Status.ToString(), report.TotalDuration.ToString("c"), TcjTelemetry.FrameworkVersion),
            TcjAspNetCoreJsonSerializerContext.Default.PublicHealthResponse,
            cancellationToken: httpContext.RequestAborted);
    }

    /// <summary>
    /// Writes a sanitized diagnostic response containing names, statuses, durations, and stable tags only.
    /// Exception messages, stack traces, data values, connection details, and provider error text are never emitted.
    /// </summary>
    /// <param name="httpContext">Current HTTP context.</param>
    /// <param name="report">Completed health report.</param>
    /// <returns>A task that completes when the sanitized JSON response has been written.</returns>
    public static Task WriteDetailedAsync(HttpContext httpContext, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(report);
        Prepare(httpContext);
        var checks = report.Entries
            .OrderBy(static item => item.Key, StringComparer.Ordinal)
            .Select(static item => new DetailedHealthCheckResponse(
                StableCheckNames.Contains(item.Key) ? item.Key : "custom",
                item.Value.Status.ToString(),
                item.Value.Duration.ToString("c"),
                item.Value.Tags
                    .Where(static tag => StableTags.Contains(tag))
                    .OrderBy(static tag => tag, StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();
        return JsonSerializer.SerializeAsync(
            httpContext.Response.Body,
            new DetailedHealthResponse(report.Status.ToString(), report.TotalDuration.ToString("c"), TcjTelemetry.FrameworkVersion, checks),
            TcjAspNetCoreJsonSerializerContext.Default.DetailedHealthResponse,
            cancellationToken: httpContext.RequestAborted);
    }

    private static void Prepare(HttpContext context)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
    }

}

internal sealed record PublicHealthResponse(string Status, string Duration, string Version);

internal sealed record DetailedHealthResponse(
    string Status,
    string Duration,
    string Version,
    DetailedHealthCheckResponse[] Checks);

internal sealed record DetailedHealthCheckResponse(
    string Name,
    string Status,
    string Duration,
    string[] Tags);
