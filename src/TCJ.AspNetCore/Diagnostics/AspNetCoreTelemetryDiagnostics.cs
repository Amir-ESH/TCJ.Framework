using System.Diagnostics;
using System.Diagnostics.Metrics;
using TCJ.Core.Diagnostics;

namespace TCJ.AspNetCore.Diagnostics;

internal static class AspNetCoreTelemetryDiagnostics
{
    internal static readonly string PackageVersion =
        TcjPackageMetadata.GetPackageVersion(typeof(TcjExceptionHandler).Assembly);

    internal static readonly ActivitySource ActivitySource = new(
        TcjDiagnosticNames.Sources.AspNetCore,
        PackageVersion);

    internal static readonly Meter Meter = new(
        TcjDiagnosticNames.Sources.AspNetCore,
        PackageVersion);

    internal static readonly Counter<long> ExceptionsHandled = Meter.CreateCounter<long>(
        TcjDiagnosticNames.Metrics.AspNetCoreExceptionsHandled,
        unit: "{exception}",
        description: "Exceptions handled by TCJ ASP.NET Core exception handling.");

    internal static readonly Histogram<double> ExceptionHandlerDuration = Meter.CreateHistogram<double>(
        TcjDiagnosticNames.Metrics.AspNetCoreExceptionHandlerDuration,
        unit: "s",
        description: "TCJ exception-handler duration in seconds.");

    internal static AspNetCoreTelemetryState StartExceptionHandling()
    {
        Activity? activity = TcjTelemetry.StartActivity(
            ActivitySource,
            TcjDiagnosticNames.Activities.AspNetCoreExceptionHandle,
            TcjDiagnosticNames.Sources.AspNetCore,
            PackageVersion,
            "exception_handle");

        bool counterEnabled = TcjTelemetry.MetricsEnabled && ExceptionsHandled.Enabled;
        bool durationEnabled = TcjTelemetry.MetricsEnabled && ExceptionHandlerDuration.Enabled;

        return new AspNetCoreTelemetryState(activity, counterEnabled, durationEnabled);
    }
}

internal readonly struct AspNetCoreTelemetryState
{
    private readonly Activity? _activity;
    private readonly bool _counterEnabled;
    private readonly bool _durationEnabled;
    private readonly long _startedAt;

    internal AspNetCoreTelemetryState(Activity? activity, bool counterEnabled, bool durationEnabled)
    {
        _activity = activity;
        _counterEnabled = counterEnabled;
        _durationEnabled = durationEnabled;
        _startedAt = durationEnabled ? Stopwatch.GetTimestamp() : 0;
    }

    internal Activity? Activity => _activity;

    internal void CompleteCanceled(Exception exception)
    {
        _activity?.SetTag(TcjDiagnosticNames.Tags.ExceptionCategory, "canceled");
        _activity?.SetTag(TcjDiagnosticNames.Tags.ExceptionType, TcjTelemetry.NormalizeTypeName(exception.GetType()));
        TcjTelemetry.CompleteCanceled(_activity);
        Record(TcjDiagnosticNames.Outcomes.Canceled, "canceled", null);
    }

    internal void CompleteFailure(Exception exception, int? httpStatusCode, string category)
    {
        _activity?.SetTag(TcjDiagnosticNames.Tags.ExceptionCategory, category);
        if (httpStatusCode.HasValue)
        {
            _activity?.SetTag(TcjDiagnosticNames.Tags.HttpStatusCode, httpStatusCode.Value);
        }

        TcjTelemetry.CompleteFailure(_activity, exception);
        Record(TcjDiagnosticNames.Outcomes.Failure, category, httpStatusCode);
    }

    private void Record(string outcome, string category, int? httpStatusCode)
    {
        if (_counterEnabled || _durationEnabled)
        {
            TagList tags = new()
            {
                { TcjDiagnosticNames.Tags.OperationOutcome, outcome },
                { TcjDiagnosticNames.Tags.ExceptionCategory, category }
            };

            if (httpStatusCode.HasValue)
            {
                tags.Add(TcjDiagnosticNames.Tags.HttpStatusCode, httpStatusCode.Value);
            }

            if (_counterEnabled)
            {
                AspNetCoreTelemetryDiagnostics.ExceptionsHandled.Add(1, tags);
            }

            if (_durationEnabled)
            {
                AspNetCoreTelemetryDiagnostics.ExceptionHandlerDuration.Record(
                    Stopwatch.GetElapsedTime(_startedAt).TotalSeconds,
                    tags);
            }
        }

        _activity?.Dispose();
    }
}
