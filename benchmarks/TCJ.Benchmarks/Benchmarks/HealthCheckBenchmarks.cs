using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.AspNetCore.HealthChecks;
using TCJ.Core.Diagnostics;
using TCJ.Core.HealthChecks;
using TCJ.DependencyInjection.HealthChecks;

namespace TCJ.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("TCJ.Core", "HealthChecks")]
public class HealthCheckBenchmarks
{
    private readonly ServiceProvider _provider;
    private readonly HealthCheckService _healthService;
    private readonly AsyncHealthCheckCache<int> _cached;
    private readonly AsyncHealthCheckCache<int> _uncached;
    private readonly DefaultHttpContext _responseContext = new();
    private readonly MemoryStream _responseBody = new();
    private readonly HealthReport _healthyReport = new(new Dictionary<string, HealthReportEntry>(), TimeSpan.Zero);

    public HealthCheckBenchmarks()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTcjHealthChecks();
        _provider = services.BuildServiceProvider();
        _healthService = _provider.GetRequiredService<HealthCheckService>();
        _cached = new AsyncHealthCheckCache<int>(TimeProvider.System, TimeSpan.FromSeconds(10));
        _uncached = new AsyncHealthCheckCache<int>(TimeProvider.System, TimeSpan.Zero);
        _cached.GetOrCreateAsync(_ => Task.FromResult(42), CancellationToken.None).GetAwaiter().GetResult();
        _responseContext.Response.Body = _responseBody;
    }

    [Benchmark(Baseline = true)]
    public Task<HealthReport> LivenessEndpointCorePath()
        => _healthService.CheckHealthAsync(registration => registration.Name == TcjHealthCheckNames.Checks.Core);

    [Benchmark]
    public Task<int> CachedReadinessPath()
        => _cached.GetOrCreateAsync(_ => Task.FromResult(42), CancellationToken.None);

    [Benchmark]
    public Task<int> UncachedReadinessPath()
        => _uncached.GetOrCreateAsync(_ => Task.FromResult(42), CancellationToken.None);

    [Benchmark]
    public async Task PublicResponseSerialization()
    {
        _responseBody.SetLength(0);
        _responseBody.Position = 0;
        await TcjHealthResponseWriter.WritePublicAsync(_responseContext, _healthyReport).ConfigureAwait(false);
    }

    [Benchmark]
    public async Task TelemetryDisabled()
    {
        TcjTelemetry.Configure(options => { options.EnableTracing = false; options.EnableMetrics = false; });
        await _healthService.CheckHealthAsync(registration => registration.Name == TcjHealthCheckNames.Checks.Core).ConfigureAwait(false);
    }

    [Benchmark]
    public async Task TelemetryEnabled()
    {
        TcjTelemetry.Configure(options => { options.EnableTracing = true; options.EnableMetrics = true; });
        await _healthService.CheckHealthAsync(registration => registration.Name == TcjHealthCheckNames.Checks.Core).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _responseBody.Dispose();
        _provider.Dispose();
        TcjTelemetry.ResetForTests();
    }
}
