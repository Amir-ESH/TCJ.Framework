namespace TCJ.Messaging.Abstractions;

/// <summary>Optional marker for application integration-message contracts.</summary>
/// <remarks>Application messages are not required to implement this interface.</remarks>
public interface IIntegrationMessage;

/// <summary>Optional logical message-contract metadata.</summary>
public interface IIntegrationMessageMetadata
{
    /// <summary>Gets the stable logical message type.</summary>
    string MessageType { get; }

    /// <summary>Gets the positive schema version.</summary>
    int MessageVersion { get; }
}
