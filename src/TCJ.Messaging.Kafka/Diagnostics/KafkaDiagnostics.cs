using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TCJ.Messaging.Kafka.Diagnostics;

internal static class KafkaDiagnostics
{
    internal const string ActivitySourceName = "TCJ.Messaging.Kafka";
    internal const string MeterName = "TCJ.Messaging.Kafka";
    private static readonly ActivitySource Source = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Published = Meter.CreateCounter<long>("tcj.kafka.messages.published");
    private static readonly Counter<long> Failed = Meter.CreateCounter<long>("tcj.kafka.messages.failed");
    private static readonly Counter<long> Consumed = Meter.CreateCounter<long>("tcj.kafka.messages.consumed");
    private static readonly Counter<long> Processed = Meter.CreateCounter<long>("tcj.kafka.messages.processed");
    private static readonly Counter<long> Retried = Meter.CreateCounter<long>("tcj.kafka.messages.retried");
    private static readonly Counter<long> Dead = Meter.CreateCounter<long>("tcj.kafka.messages.dead_lettered");
    private static readonly Counter<long> Commits = Meter.CreateCounter<long>("tcj.kafka.offset.commits");
    private static readonly Counter<long> CommitFailures = Meter.CreateCounter<long>("tcj.kafka.offset.commit_failures");
    private static readonly Counter<long> Rebalances = Meter.CreateCounter<long>("tcj.kafka.rebalances");
    private static readonly UpDownCounter<long> AssignedPartitions = Meter.CreateUpDownCounter<long>("tcj.kafka.assigned_partitions");
    private static readonly UpDownCounter<long> PausedPartitions = Meter.CreateUpDownCounter<long>("tcj.kafka.paused_partitions");
    private static readonly Histogram<double> PublishDuration = Meter.CreateHistogram<double>("tcj.kafka.publish.duration", "ms");
    private static readonly Histogram<double> ProcessingDuration = Meter.CreateHistogram<double>("tcj.kafka.processing.duration", "ms");
    internal static Activity? Start(string name, ActivityKind kind, string? destination = null) { Activity? a = Source.StartActivity(name, kind); if (a is not null && destination is not null) a.SetTag("messaging.destination.name", destination); return a; }
    internal static void PublishOk(double ms) { Published.Add(1); PublishDuration.Record(ms); }
    internal static void PublishFail(double ms) { Failed.Add(1); PublishDuration.Record(ms); }
    internal static void Consume() => Consumed.Add(1);
    internal static void Process(double ms) { Processed.Add(1); ProcessingDuration.Record(ms); }
    internal static void Retry() => Retried.Add(1);
    internal static void DeadLetter() => Dead.Add(1);
    internal static void Commit() => Commits.Add(1);
    internal static void CommitFailure() => CommitFailures.Add(1);
    internal static void Rebalance() => Rebalances.Add(1);
    internal static void Assigned(int count) { AssignedPartitions.Add(count); using Activity? _ = Start("tcj.kafka.partition.assign", ActivityKind.Consumer); }
    internal static void Revoked(int count) { AssignedPartitions.Add(-count); using Activity? _ = Start("tcj.kafka.partition.revoke", ActivityKind.Consumer); }
    internal static void Paused(int count) { PausedPartitions.Add(count); using Activity? _ = Start("tcj.kafka.pause", ActivityKind.Consumer); }
    internal static void Resumed(int count) { PausedPartitions.Add(-count); using Activity? _ = Start("tcj.kafka.resume", ActivityKind.Consumer); }
    internal static void CommitActivity() { using Activity? _ = Start("tcj.kafka.offset.commit", ActivityKind.Consumer); }
}
