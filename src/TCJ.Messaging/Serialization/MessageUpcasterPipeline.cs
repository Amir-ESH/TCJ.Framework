using TCJ.Messaging.Configuration;

namespace TCJ.Messaging.Serialization;

internal sealed class MessageUpcasterPipeline
{
    private readonly IReadOnlyDictionary<(string MessageType, int SourceVersion), IMessageUpcaster> _transitions;
    private readonly int _maximumPayloadBytes;

    public MessageUpcasterPipeline(IEnumerable<IMessageUpcaster> upcasters, TcjMessagingOptions options)
    {
        ArgumentNullException.ThrowIfNull(upcasters);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _maximumPayloadBytes = options.MaximumPayloadBytes;
        var transitions = new Dictionary<(string, int), IMessageUpcaster>();
        foreach (IMessageUpcaster upcaster in upcasters)
        {
            ArgumentNullException.ThrowIfNull(upcaster);
            MessagingValidation.ValidateMessageType(upcaster.MessageType, nameof(upcasters), 128);
            MessagingValidation.ValidateVersion(upcaster.SourceVersion, nameof(upcasters));
            MessagingValidation.ValidateVersion(upcaster.TargetVersion, nameof(upcasters));
            if (upcaster.TargetVersion <= upcaster.SourceVersion)
                throw new InvalidOperationException($"Message upcaster '{upcaster.GetType().FullName}' must advance '{upcaster.MessageType}' from a lower source version to a higher target version.");
            if (!transitions.TryAdd((upcaster.MessageType, upcaster.SourceVersion), upcaster))
                throw new InvalidOperationException($"More than one message upcaster is registered for '{upcaster.MessageType}' version {upcaster.SourceVersion}.");
        }
        _transitions = transitions;
        ValidateAcyclicTransitions();
    }

    public ReadOnlyMemory<byte> Upcast(string messageType, int sourceVersion, int targetVersion, ReadOnlyMemory<byte> payload)
    {
        MessagingValidation.ValidateMessageType(messageType, nameof(messageType), 128);
        MessagingValidation.ValidateVersion(sourceVersion, nameof(sourceVersion));
        MessagingValidation.ValidateVersion(targetVersion, nameof(targetVersion));
        if (sourceVersion > targetVersion)
            throw new InvalidOperationException($"Message '{messageType}' version {sourceVersion} is newer than the selected contract version {targetVersion}.");
        ReadOnlyMemory<byte> currentPayload = payload;
        int currentVersion = sourceVersion;
        var visited = new HashSet<int>();
        while (currentVersion < targetVersion)
        {
            if (!visited.Add(currentVersion))
                throw new InvalidOperationException($"Message upcaster chain for '{messageType}' contains a cycle at version {currentVersion}.");
            if (!_transitions.TryGetValue((messageType, currentVersion), out IMessageUpcaster? upcaster))
                throw new InvalidOperationException($"No message upcaster is registered for '{messageType}' version {currentVersion} while targeting version {targetVersion}.");
            if (upcaster.TargetVersion > targetVersion)
                throw new InvalidOperationException($"Message upcaster for '{messageType}' version {currentVersion} advances beyond selected target version {targetVersion}.");
            currentPayload = upcaster.Upcast(currentPayload);
            if (currentPayload.Length > _maximumPayloadBytes)
                throw new ArgumentException($"Upcast message payload exceeds the configured {_maximumPayloadBytes}-byte limit.", nameof(payload));
            currentVersion = upcaster.TargetVersion;
        }
        return currentPayload;
    }

    private void ValidateAcyclicTransitions()
    {
        foreach (KeyValuePair<(string MessageType, int SourceVersion), IMessageUpcaster> transition in _transitions)
        {
            string messageType = transition.Key.MessageType;
            int currentVersion = transition.Key.SourceVersion;
            var visited = new HashSet<int>();
            while (_transitions.TryGetValue((messageType, currentVersion), out IMessageUpcaster? next))
            {
                if (!visited.Add(currentVersion))
                    throw new InvalidOperationException($"Message upcaster registrations for '{messageType}' contain a cycle at version {currentVersion}.");
                currentVersion = next.TargetVersion;
            }
        }
    }
}
