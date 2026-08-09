using System.Diagnostics;
using System.Diagnostics.Metrics;
using TCJ.Core.Diagnostics;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;

namespace TCJ.EntityFrameworkCore.SqlServer.Diagnostics;

internal static class SqlServerTelemetryDiagnostics
{
    internal static readonly string PackageVersion =
        TcjPackageMetadata.GetPackageVersion(typeof(SqlServerServiceCollectionExtensions).Assembly);

    internal static readonly ActivitySource ActivitySource = new(
        TcjDiagnosticNames.Sources.EntityFrameworkCoreSqlServer,
        PackageVersion);

    internal static readonly Meter Meter = new(
        TcjDiagnosticNames.Sources.EntityFrameworkCoreSqlServer,
        PackageVersion);

    internal static Activity? StartConfigureActivity()
    {
        Activity? activity = TcjTelemetry.StartActivity(
            ActivitySource,
            TcjDiagnosticNames.Activities.SqlServerConfigure,
            TcjDiagnosticNames.Sources.EntityFrameworkCoreSqlServer,
            PackageVersion,
            "configure");

        activity?.SetTag(
            TcjDiagnosticNames.Tags.DatabaseProvider,
            TcjDiagnosticNames.Providers.SqlServer);

        return activity;
    }
}
