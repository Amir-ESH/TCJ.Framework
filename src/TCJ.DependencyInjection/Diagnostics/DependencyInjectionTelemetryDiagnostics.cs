using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.Diagnostics;
using TCJ.DependencyInjection.Extensions;

namespace TCJ.DependencyInjection.Diagnostics;

internal static class DependencyInjectionTelemetryDiagnostics
{
    internal static readonly string PackageVersion =
        TcjPackageMetadata.GetPackageVersion(typeof(ServiceCollectionExtensions).Assembly);

    internal static readonly ActivitySource ActivitySource = new(
        TcjDiagnosticNames.Sources.DependencyInjection,
        PackageVersion);

    // A meter is intentionally defined even though registration-time metrics are
    // not emitted. This keeps the stable meter identity available without leaving
    // registration instruments active during steady-state runtime.
    internal static readonly Meter Meter = new(
        TcjDiagnosticNames.Sources.DependencyInjection,
        PackageVersion);

    internal static Type[] ObserveScan(int assemblyCount, Func<Type[]> scan)
    {
        ArgumentNullException.ThrowIfNull(scan);

        using Activity? activity = StartActivity(
            TcjDiagnosticNames.Activities.DependencyInjectionScan,
            "scan");

        try
        {
            Type[] implementationTypes = scan();
            if (activity is not null)
            {
                activity.SetTag(TcjDiagnosticNames.Tags.AssemblyCount, assemblyCount);
                activity.SetTag(TcjDiagnosticNames.Tags.DiscoveredTypeCount, implementationTypes.Length);
            }

            TcjTelemetry.CompleteSuccess(activity);
            return implementationTypes;
        }
        catch (Exception exception)
        {
            TcjTelemetry.CompleteFailure(activity, exception);
            throw;
        }
    }

    internal static void ObserveRegistration(
        IServiceCollection services,
        Action registration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registration);

        using Activity? activity = StartActivity(
            TcjDiagnosticNames.Activities.DependencyInjectionRegister,
            "register");

        int serviceCountBefore = activity is null ? 0 : services.Count;
        try
        {
            registration();

            if (activity is not null)
            {
                activity.SetTag(
                    TcjDiagnosticNames.Tags.RegisteredServiceCount,
                    services.Count - serviceCountBefore);
            }

            TcjTelemetry.CompleteSuccess(activity);
        }
        catch (Exception exception)
        {
            TcjTelemetry.CompleteFailure(activity, exception);
            throw;
        }
    }

    private static Activity? StartActivity(string activityName, string operationName) =>
        TcjTelemetry.StartActivity(
            ActivitySource,
            activityName,
            TcjDiagnosticNames.Sources.DependencyInjection,
            PackageVersion,
            operationName);
}
