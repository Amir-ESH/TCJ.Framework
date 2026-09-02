using System.Threading;
using TCJ.Core.Inbox;

namespace TCJ.EntityFrameworkCore.Inbox.Processing;

internal sealed class InboxMessageContextAccessor : IInboxMessageContextAccessor
{
    private static readonly AsyncLocal<InboxMessageContext?> CurrentContext = new();
    public InboxMessageContext? Current => CurrentContext.Value;
    internal IDisposable Push(InboxMessageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        InboxMessageContext? previous = CurrentContext.Value;
        CurrentContext.Value = context;
        return new Scope(previous);
    }
    private sealed class Scope(InboxMessageContext? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            CurrentContext.Value = previous;
            _disposed = true;
        }
    }
}
