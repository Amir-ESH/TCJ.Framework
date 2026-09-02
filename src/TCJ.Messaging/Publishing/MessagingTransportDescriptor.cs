namespace TCJ.Messaging.Publishing;

/// <summary>Declared transport ordering guarantee.</summary>
public enum MessagingOrderingGuarantee
{
    /// <summary>No transport ordering guarantee is declared.</summary>
    None = 0,
    /// <summary>Ordering is guaranteed only within one partition.</summary>
    PerPartition = 1,
    /// <summary>Ordering is guaranteed only within one transport session or equivalent key.</summary>
    PerSession = 2,
    /// <summary>The transport attempts ordering without a strict guarantee.</summary>
    BestEffort = 3
}

/// <summary>Capabilities that callers must not assume unless declared by the selected adapter.</summary>
public sealed record MessagingTransportCapabilities
{
    /// <summary>Gets whether native batch publishing is supported.</summary>
    public bool SupportsBatchPublish { get; init; }
    /// <summary>Gets whether scheduled publication is supported.</summary>
    public bool SupportsScheduling { get; init; }
    /// <summary>Gets whether message time-to-live is supported.</summary>
    public bool SupportsTimeToLive { get; init; }
    /// <summary>Gets whether explicit dead-letter settlement is supported.</summary>
    public bool SupportsDeadLetter { get; init; }
    /// <summary>Gets whether defer settlement is supported.</summary>
    public bool SupportsDefer { get; init; }
    /// <summary>Gets whether an ordering capability is available.</summary>
    public bool SupportsOrderedDelivery { get; init; }
    /// <summary>Gets whether explicit partitioning is supported.</summary>
    public bool SupportsPartitioning { get; init; }
    /// <summary>Gets whether adapter-owned messaging transactions are supported.</summary>
    public bool SupportsTransactions { get; init; }
    /// <summary>Gets whether a peek-lock or equivalent settlement model is supported.</summary>
    public bool SupportsPeekLock { get; init; }
    /// <summary>Gets the adapter's declared ordering guarantee.</summary>
    public MessagingOrderingGuarantee OrderingGuarantee { get; init; }
    /// <summary>Gets the adapter's maximum payload bytes when known.</summary>
    public int? MaximumPayloadBytes { get; init; }
    /// <summary>Gets the adapter's maximum combined header bytes when known.</summary>
    public int? MaximumHeaderBytes { get; init; }
    /// <summary>Gets the adapter's maximum native batch size when known.</summary>
    public int? MaximumBatchSize { get; init; }
}

/// <summary>Stable adapter descriptor validated before use.</summary>
public sealed record MessagingTransportDescriptor
{
    /// <summary>Gets the bounded stable adapter name.</summary>
    public required string Name { get; init; }
    /// <summary>Gets the adapter implementation or protocol version.</summary>
    public required string Version { get; init; }
    /// <summary>Gets explicitly declared adapter capabilities.</summary>
    public required MessagingTransportCapabilities Capabilities { get; init; }
}

/// <summary>Explicit exception used when an adapter operation is not supported.</summary>
public sealed class MessagingCapabilityException : NotSupportedException
{
    /// <summary>Creates an unsupported-capability exception without transport-specific details.</summary>
    /// <param name="capability">Stable capability identifier.</param>
    public MessagingCapabilityException(string capability)
        : base($"The selected messaging transport does not support capability '{capability}'.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        Capability = capability;
    }

    /// <summary>Gets the stable unsupported capability identifier.</summary>
    public string Capability { get; }
}
