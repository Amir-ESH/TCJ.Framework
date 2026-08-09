using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.Diagnostics;
using TCJ.DependencyInjection.Extensions;

namespace TCJ.Observability.Tests;

public sealed class DependencyInjectionTelemetryTests : IDisposable
{
    public DependencyInjectionTelemetryTests() => TcjTelemetry.ResetForTests();

    [Fact]
    public void Registration_emits_one_register_span_and_one_nested_scan_span()
    {
        using var collector = new ActivityCollector(TcjDiagnosticNames.Sources.DependencyInjection);
        using var request = new Activity("test.startup").Start();
        var services = new ServiceCollection();

        services.AddTcjDependencyInjection(typeof(ServiceCollectionExtensions).Assembly);

        Activity register = Assert.Single(
            collector.Activities,
            activity => activity.OperationName == TcjDiagnosticNames.Activities.DependencyInjectionRegister);
        Activity scan = Assert.Single(
            collector.Activities,
            activity => activity.OperationName == TcjDiagnosticNames.Activities.DependencyInjectionScan);

        Assert.Equal(request.TraceId, register.TraceId);
        Assert.Equal(request.SpanId, register.ParentSpanId);
        Assert.Equal(register.TraceId, scan.TraceId);
        Assert.Equal(register.SpanId, scan.ParentSpanId);
        Assert.Equal(ActivityStatusCode.Ok, register.Status);
        Assert.Equal(ActivityStatusCode.Ok, scan.Status);
        Assert.True(Convert.ToInt32(Tag(scan, TcjDiagnosticNames.Tags.AssemblyCount)) >= 1);
        Assert.True(Convert.ToInt32(Tag(scan, TcjDiagnosticNames.Tags.DiscoveredTypeCount)) >= 1);
        Assert.True(Convert.ToInt32(Tag(register, TcjDiagnosticNames.Tags.RegisteredServiceCount)) >= 1);
    }

    private static object? Tag(Activity activity, string name) =>
        activity.TagObjects.FirstOrDefault(tag => tag.Key == name).Value;

    public void Dispose() => TcjTelemetry.ResetForTests();
}
