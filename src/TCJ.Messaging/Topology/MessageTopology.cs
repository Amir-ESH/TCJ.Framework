using TCJ.Messaging.Configuration;

namespace TCJ.Messaging.Topology;

/// <summary>Explicit transport-neutral destination.</summary>
public sealed record MessageDestination
{
    /// <summary>Gets the required stable destination name.</summary>
    public required string Name { get; init; }
    /// <summary>Gets the optional subscription or consumer-group name.</summary>
    public string? Subscription { get; init; }
}

/// <summary>Deterministic broker-neutral topology naming strategy.</summary>
public interface IMessageTopologyNamingStrategy
{
    /// <summary>Resolves a stable destination.</summary><param name="messageType">Logical type.</param><param name="messageVersion">Version.</param><returns>Destination.</returns>
    string GetDestination(string messageType, int messageVersion);
    /// <summary>Resolves a stable subscription.</summary><param name="consumerName">Consumer.</param><returns>Subscription.</returns>
    string GetSubscription(string consumerName);
}

/// <summary>Default stable topology strategy; environment prefixes are opt-in configuration.</summary>
public sealed class DefaultMessageTopologyNamingStrategy : IMessageTopologyNamingStrategy
{
    private readonly TcjMessagingOptions _options;
    /// <summary>Creates the default naming strategy.</summary><param name="options">Messaging options.</param>
    public DefaultMessageTopologyNamingStrategy(TcjMessagingOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }
    /// <inheritdoc />
    public string GetDestination(string messageType, int messageVersion)
    {
        MessagingValidation.ValidateMessageType(messageType, nameof(messageType), _options.MaximumMessageTypeLength);
        MessagingValidation.ValidateVersion(messageVersion, nameof(messageVersion));
        return ValidateResult($"{_options.EnvironmentPrefix}{messageType}.v{messageVersion}");
    }
    /// <inheritdoc />
    public string GetSubscription(string consumerName)
    {
        MessagingValidation.ValidateTopologyName(consumerName, nameof(consumerName), _options.MaximumDestinationNameLength);
        return ValidateResult($"{_options.EnvironmentPrefix}{consumerName}");
    }
    private string ValidateResult(string value)
    {
        MessagingValidation.ValidateTopologyName(value, nameof(value), _options.MaximumDestinationNameLength);
        return value;
    }
}
