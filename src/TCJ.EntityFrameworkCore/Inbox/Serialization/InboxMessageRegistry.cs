using TCJ.Core.Inbox;

namespace TCJ.EntityFrameworkCore.Inbox.Serialization;

internal sealed class InboxMessageRegistry
{
    private readonly IReadOnlyDictionary<(string Name, int Version), InboxResolvedRegistration> _byContract;
    private readonly IReadOnlyDictionary<Type, (string Name, int Version)> _byType;

    public InboxMessageRegistry(IEnumerable<InboxMessageRegistration> messages, IEnumerable<InboxHandlerRegistration> handlers)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(handlers);
        InboxMessageRegistration[] messageArray = messages.ToArray();
        InboxHandlerRegistration[] handlerArray = handlers.ToArray();
        var handlerByType = new Dictionary<Type, InboxHandlerRegistration>();
        foreach (InboxHandlerRegistration handler in handlerArray)
        {
            if (handlerByType.TryGetValue(handler.MessageType, out InboxHandlerRegistration? existing) && existing.HandlerType != handler.HandlerType)
            {
                throw new InvalidOperationException($"Inbox message CLR type '{handler.MessageType.FullName}' has ambiguous handlers '{existing.HandlerType.FullName}' and '{handler.HandlerType.FullName}'.");
            }
            handlerByType[handler.MessageType] = handler;
        }

        var byContract = new Dictionary<(string, int), InboxResolvedRegistration>();
        var byType = new Dictionary<Type, (string, int)>();
        foreach (InboxMessageRegistration message in messageArray)
        {
            ValidateMessageName(message.MessageName);
            if (message.Version <= 0) throw new InvalidOperationException("Inbox message versions must be greater than zero.");
            if (!handlerByType.TryGetValue(message.MessageType, out InboxHandlerRegistration? handler))
            {
                throw new InvalidOperationException($"Inbox message '{message.MessageName}' v{message.Version} has no registered handler for CLR type '{message.MessageType.FullName}'.");
            }
            var key = (message.MessageName, message.Version);
            if (byContract.TryGetValue(key, out InboxResolvedRegistration? existing) && existing.MessageType != message.MessageType)
            {
                throw new InvalidOperationException($"Inbox message contract '{message.MessageName}' v{message.Version} is registered for more than one CLR type.");
            }
            if (byType.TryGetValue(message.MessageType, out (string Name, int Version) existingType) && existingType != key)
            {
                throw new InvalidOperationException($"Inbox CLR type '{message.MessageType.FullName}' is registered for more than one wire contract.");
            }
            byContract[key] = new InboxResolvedRegistration(message.MessageType, message.MessageName, message.Version, handler);
            byType[message.MessageType] = key;
        }
        _byContract = byContract;
        _byType = byType;
    }

    internal InboxResolvedRegistration Resolve(string messageName, int version)
    {
        ValidateMessageName(messageName);
        if (_byContract.TryGetValue((messageName, version), out InboxResolvedRegistration? registration)) return registration;
        if (_byContract.Keys.Any(key => string.Equals(key.Name, messageName, StringComparison.Ordinal)))
        {
            throw new InboxUnknownMessageVersionException(messageName, version);
        }
        throw new InboxUnknownMessageTypeException(messageName);
    }

    internal bool IsRegistered(string messageName, int version) => _byContract.ContainsKey((messageName, version));
    internal IReadOnlyCollection<string> RegisteredNames => _byContract.Keys.Select(static key => key.Name).Distinct(StringComparer.Ordinal).ToArray();

    internal static void ValidateMessageName(string messageName) => TcjInboxOptions.ValidateContractName(messageName, nameof(messageName), 128);
}
