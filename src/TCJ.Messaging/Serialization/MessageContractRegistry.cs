using TCJ.Messaging.Configuration;

namespace TCJ.Messaging.Serialization;

/// <summary>Immutable registry of explicit logical message contracts.</summary>
public sealed class MessageContractRegistry : IMessageContractRegistry
{
    private readonly IReadOnlyDictionary<(string MessageType, int Version), MessagingMessageContract> _byWire;

    /// <summary>Creates and validates an immutable contract registry.</summary>
    /// <param name="contracts">Explicitly registered contracts.</param>
    public MessageContractRegistry(IEnumerable<MessagingMessageContract> contracts)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        var map = new Dictionary<(string, int), MessagingMessageContract>();
        foreach (MessagingMessageContract contract in contracts)
        {
            ArgumentNullException.ThrowIfNull(contract);
            MessagingValidation.ValidateMessageType(contract.MessageType, nameof(contracts), 128);
            MessagingValidation.ValidateVersion(contract.MessageVersion, nameof(contracts));
            if (!map.TryAdd((contract.MessageType, contract.MessageVersion), contract))
                throw new InvalidOperationException($"Duplicate messaging contract '{contract.MessageType}' v{contract.MessageVersion}.");
        }
        _byWire = map;
        Contracts = map.Values.ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyCollection<MessagingMessageContract> Contracts { get; }

    /// <inheritdoc />
    public MessagingMessageContract Resolve(string messageType, int messageVersion)
    {
        MessagingValidation.ValidateMessageType(messageType, nameof(messageType), 128);
        MessagingValidation.ValidateVersion(messageVersion, nameof(messageVersion));
        return _byWire.TryGetValue((messageType, messageVersion), out MessagingMessageContract? contract)
            ? contract
            : throw new InvalidOperationException($"No messaging contract is registered for '{messageType}' v{messageVersion}.");
    }

    /// <inheritdoc />
    public MessagingMessageContract Resolve(Type clrType, string messageType, int messageVersion)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        MessagingMessageContract contract = Resolve(messageType, messageVersion);
        if (contract.ClrType != clrType)
            throw new InvalidOperationException($"Messaging contract '{messageType}' v{messageVersion} is registered for '{contract.ClrType.FullName}', not '{clrType.FullName}'.");
        return contract;
    }
}
