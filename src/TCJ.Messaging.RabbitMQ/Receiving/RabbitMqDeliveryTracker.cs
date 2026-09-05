namespace TCJ.Messaging.RabbitMQ.Receiving;

internal sealed class RabbitMqDeliveryTracker
{
    private readonly object _sync = new();
    private int _active;
    private TaskCompletionSource _empty = Completed();

    internal void Add()
    {
        lock (_sync)
        {
            if (_active++ == 0) _empty = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    internal void Complete()
    {
        lock (_sync)
        {
            if (_active <= 0) return;
            if (--_active == 0) _empty.TrySetResult();
        }
    }

    internal Task WaitForEmptyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task task;
        lock (_sync) task = _empty.Task;
        return task.WaitAsync(timeout, cancellationToken);
    }

    private static TaskCompletionSource Completed()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        result.SetResult();
        return result;
    }
}
