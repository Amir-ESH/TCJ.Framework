using System.Threading;
using TCJ.Core.Outbox;

namespace TCJ.EntityFrameworkCore.Outbox.Processing;

internal sealed class OutboxMessageContextAccessor : IOutboxMessageContextAccessor
{
    private readonly AsyncLocal<OutboxMessageContext?> _current = new();

    public OutboxMessageContext? Current => _current.Value;

    internal IDisposable Push(OutboxMessageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        OutboxMessageContext? previous = _current.Value;
        _current.Value = context;
        return new RestoreScope(this, previous);
    }

    private sealed class RestoreScope(OutboxMessageContextAccessor accessor, OutboxMessageContext? previous) : IDisposable
    {
        private OutboxMessageContextAccessor? _accessor = accessor;
        public void Dispose()
        {
            OutboxMessageContextAccessor? current = Interlocked.Exchange(ref _accessor, null);
            if (current is not null)
            {
                current._current.Value = previous;
            }
        }
    }
}
