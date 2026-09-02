namespace TCJ.Core.Inbox;

/// <summary>Deserializes an inbound payload only as a CLR type selected by the registered Inbox contract.</summary>
public interface IInboxSerializer
{
    /// <summary>Deserializes a bounded payload as the explicitly registered CLR type.</summary>
    /// <param name="messageType">CLR type selected by the stable type/version registry.</param>
    /// <param name="payload">Serialized inbound payload.</param>
    /// <returns>Deserialized message instance.</returns>
    object Deserialize(Type messageType, string payload);
}
