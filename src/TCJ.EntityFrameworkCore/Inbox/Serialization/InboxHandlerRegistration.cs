using TCJ.Core.Inbox;

namespace TCJ.EntityFrameworkCore.Inbox.Serialization;

internal sealed class InboxHandlerRegistration
{
    internal InboxHandlerRegistration(Type messageType, Type handlerType, Func<IServiceProvider, object, InboxMessageContext, CancellationToken, Task> invoke)
    {
        MessageType = messageType;
        HandlerType = handlerType;
        Invoke = invoke;
    }
    internal Type MessageType { get; }
    internal Type HandlerType { get; }
    internal Func<IServiceProvider, object, InboxMessageContext, CancellationToken, Task> Invoke { get; }
}
