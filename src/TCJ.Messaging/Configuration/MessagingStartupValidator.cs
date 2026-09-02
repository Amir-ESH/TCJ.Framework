using TCJ.Core.Inbox;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;
using TCJ.Messaging.Serialization;
using TCJ.Messaging.Topology;

namespace TCJ.Messaging.Configuration;

/// <summary>Validates messaging registrations and bounded adapter capabilities before use.</summary>
public interface IMessagingStartupValidator
{
    /// <summary>Validates the configured messaging boundary.</summary>
    /// <param name="cancellationToken">Token used to cancel startup validation.</param>
    Task ValidateAsync(CancellationToken cancellationToken = default);
}

/// <summary>Default fail-closed startup validator.</summary>
public sealed class MessagingStartupValidator : IMessagingStartupValidator
{
    private readonly MessagingTransportDescriptor[] _descriptors;
    private readonly IMessagingTransportPublisher[] _publishers;
    private readonly IMessageReceiver[] _receivers;
    private readonly IInboxPipeline[] _inboxPipelines;
    private readonly IMessageContractRegistry _registry;
    private readonly IMessageSerializer _serializer;
    private readonly IMessageTopologyNamingStrategy _topology;
    private readonly TcjMessagingOptions _options;

    /// <summary>Creates the startup validator from all candidate registrations.</summary>
    /// <param name="descriptors">Registered transport descriptors.</param>
    /// <param name="publishers">Registered transport publishers.</param>
    /// <param name="receivers">Registered transport receivers.</param>
    /// <param name="inboxPipelines">Registered transactional Inbox pipelines.</param>
    /// <param name="registry">Logical message-contract registry.</param>
    /// <param name="serializer">Transport-neutral message serializer.</param>
    /// <param name="topology">Destination naming strategy.</param>
    /// <param name="options">Messaging options to validate.</param>
    public MessagingStartupValidator(IEnumerable<MessagingTransportDescriptor> descriptors,
        IEnumerable<IMessagingTransportPublisher> publishers, IEnumerable<IMessageReceiver> receivers,
        IEnumerable<IInboxPipeline> inboxPipelines, IMessageContractRegistry registry, IMessageSerializer serializer,
        IMessageTopologyNamingStrategy topology, TcjMessagingOptions options)
    {
        _descriptors = descriptors?.ToArray() ?? throw new ArgumentNullException(nameof(descriptors));
        _publishers = publishers?.ToArray() ?? throw new ArgumentNullException(nameof(publishers));
        _receivers = receivers?.ToArray() ?? throw new ArgumentNullException(nameof(receivers));
        _inboxPipelines = inboxPipelines?.ToArray() ?? throw new ArgumentNullException(nameof(inboxPipelines));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _options.Validate();
        _ = _serializer;
        if (_descriptors.Length != 1) throw new InvalidOperationException("Exactly one messaging transport descriptor must be registered.");
        if (_publishers.Length != 1) throw new InvalidOperationException("Exactly one messaging transport publisher must be registered.");
        if (_options.EnableConsumer)
        {
            if (_receivers.Length != 1) throw new InvalidOperationException("Exactly one messaging receiver is required when consumer processing is enabled.");
            if (_inboxPipelines.Length != 1) throw new InvalidOperationException("Exactly one transactional Inbox pipeline is required when consumer processing is enabled.");
        }
        MessagingTransportDescriptor descriptor = _descriptors[0];
        MessagingValidation.ValidateIdentifier(descriptor.Name, nameof(descriptor.Name), 128);
        MessagingValidation.ValidateIdentifier(descriptor.Version, nameof(descriptor.Version), 64);
        ArgumentNullException.ThrowIfNull(descriptor.Capabilities);
        if (descriptor.Capabilities.MaximumPayloadBytes is <= 0 || descriptor.Capabilities.MaximumHeaderBytes is <= 0)
            throw new InvalidOperationException("Adapter-declared limits must be positive when specified.");
        if (descriptor.Capabilities.MaximumPayloadBytes is int transportMax && _options.MaximumPayloadBytes > transportMax)
            throw new InvalidOperationException("Configured payload limit exceeds the selected transport capability.");
        if (descriptor.Capabilities.MaximumHeaderBytes is int transportHeaderMax && _options.MaximumHeaderBytes > transportHeaderMax)
            throw new InvalidOperationException("Configured header limit exceeds the selected transport capability.");

        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MessagingMessageContract contract in _registry.Contracts)
        {
            string destination = _topology.GetDestination(contract.MessageType, contract.MessageVersion);
            if (!destinations.Add(destination)) throw new InvalidOperationException($"Messaging topology collision detected for destination '{destination}'.");
        }
        return Task.CompletedTask;
    }
}
