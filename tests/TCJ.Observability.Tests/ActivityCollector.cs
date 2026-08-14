using System.Collections.Concurrent;
using System.Diagnostics;

namespace TCJ.Observability.Tests;

internal sealed class ActivityCollector : IDisposable
{
    private readonly ConcurrentQueue<Activity> _activities = new();
    private readonly ActivityListener _listener;

    internal ActivityCollector(params string[] sourceNames)
    {
        var names = new HashSet<string>(sourceNames, StringComparer.Ordinal);
        _listener = new ActivityListener
        {
            ShouldListenTo = source => names.Contains(source.Name),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _activities.Enqueue(activity)
        };

        ActivitySource.AddActivityListener(_listener);
    }

    internal IReadOnlyList<Activity> Activities => _activities.ToArray();

    public void Dispose() => _listener.Dispose();
}
