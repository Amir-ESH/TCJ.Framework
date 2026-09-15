using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TCJ.Messaging.Sagas.Diagnostics;

internal static class SagaDiagnostics
{
    private static readonly ActivitySource Source = new(TcjSagaDiagnosticNames.Source);
    private static readonly Meter Meter = new(TcjSagaDiagnosticNames.Source);
    private static readonly Counter<long> Transitions = Meter.CreateCounter<long>(TcjSagaDiagnosticNames.Metrics.Transitions);
    private static readonly Counter<long> Completed = Meter.CreateCounter<long>(TcjSagaDiagnosticNames.Metrics.Completed);
    private static readonly Counter<long> Failed = Meter.CreateCounter<long>(TcjSagaDiagnosticNames.Metrics.Failed);
    private static readonly Counter<long> TimerExecutions = Meter.CreateCounter<long>(TcjSagaDiagnosticNames.Metrics.TimerExecutions);
    private static readonly Counter<long> TimerRetries = Meter.CreateCounter<long>(TcjSagaDiagnosticNames.Metrics.TimerRetries);
    private static readonly Counter<long> CompensationExecutions = Meter.CreateCounter<long>(TcjSagaDiagnosticNames.Metrics.CompensationExecutions);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(TcjSagaDiagnosticNames.Metrics.Duration, "ms");

    internal static Activity? Start(string name, string sagaType, int definitionVersion, string operation, string? messageType = null, string? timerName = null)
    {
        Activity? activity = Source.StartActivity(name, ActivityKind.Internal);
        activity?.SetTag(TcjSagaDiagnosticNames.Tags.SagaType, Bound(sagaType, 128));
        activity?.SetTag(TcjSagaDiagnosticNames.Tags.DefinitionVersion, definitionVersion);
        activity?.SetTag(TcjSagaDiagnosticNames.Tags.Operation, Bound(operation, 64));
        if (messageType is not null) activity?.SetTag(TcjSagaDiagnosticNames.Tags.MessageType, Bound(messageType, 128));
        if (timerName is not null) activity?.SetTag(TcjSagaDiagnosticNames.Tags.TimerName, Bound(timerName, 128));
        return activity;
    }

    internal static void Complete(Activity? activity, string sagaType, int definitionVersion, string operation, string outcome, double elapsedMilliseconds, string? failureType = null)
    {
        activity?.SetTag(TcjSagaDiagnosticNames.Tags.Outcome, Bound(outcome, 64));
        if (failureType is not null) activity?.SetTag(TcjSagaDiagnosticNames.Tags.FailureType, Bound(failureType, 128));
        activity?.SetStatus(outcome == "success" || outcome == "ignored" ? ActivityStatusCode.Ok : ActivityStatusCode.Error, failureType);
        var tags = new TagList
        {
            { TcjSagaDiagnosticNames.Tags.SagaType, Bound(sagaType, 128) },
            { TcjSagaDiagnosticNames.Tags.DefinitionVersion, definitionVersion },
            { TcjSagaDiagnosticNames.Tags.Operation, Bound(operation, 64) },
            { TcjSagaDiagnosticNames.Tags.Outcome, Bound(outcome, 64) }
        };
        Transitions.Add(1, tags);
        Duration.Record(elapsedMilliseconds, tags);
    }

    internal static void RecordCompleted(string sagaType) => Completed.Add(1, new TagList { { TcjSagaDiagnosticNames.Tags.SagaType, Bound(sagaType, 128) } });
    internal static void RecordFailed(string sagaType) => Failed.Add(1, new TagList { { TcjSagaDiagnosticNames.Tags.SagaType, Bound(sagaType, 128) } });
    internal static void RecordTimer(string sagaType, string outcome, bool retry)
    {
        var tags = new TagList { { TcjSagaDiagnosticNames.Tags.SagaType, Bound(sagaType, 128) }, { TcjSagaDiagnosticNames.Tags.Outcome, Bound(outcome, 64) } };
        TimerExecutions.Add(1, tags);
        if (retry) TimerRetries.Add(1, tags);
    }
    internal static void RecordCompensation(string sagaType, string outcome) => CompensationExecutions.Add(1, new TagList { { TcjSagaDiagnosticNames.Tags.SagaType, Bound(sagaType, 128) }, { TcjSagaDiagnosticNames.Tags.Outcome, Bound(outcome, 64) } });
    private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}
