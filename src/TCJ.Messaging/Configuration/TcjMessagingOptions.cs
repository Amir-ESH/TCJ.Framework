namespace TCJ.Messaging.Configuration;

/// <summary>Configures bounded transport-neutral messaging behavior.</summary>
public sealed class TcjMessagingOptions
{
    /// <summary>Whether startup validation requires one receiver and one transactional Inbox pipeline.</summary>
    public bool EnableConsumer { get; set; }

    /// <summary>Maximum number of concurrently executing received messages.</summary>
    public int MaximumConcurrentMessages { get; set; } = 8;

    /// <summary>Maximum number of messages buffered by a transport before backpressure is applied.</summary>
    public int MaximumBufferedMessages { get; set; } = 32;

    /// <summary>Maximum serialized payload size accepted by TCJ before adapter limits are considered.</summary>
    public int MaximumPayloadBytes { get; set; } = 1024 * 1024;

    /// <summary>Maximum combined UTF-8 header bytes accepted by TCJ.</summary>
    public int MaximumHeaderBytes { get; set; } = 16 * 1024;

    /// <summary>Maximum number of headers accepted by TCJ.</summary>
    public int MaximumHeaderCount { get; set; } = 64;

    /// <summary>Maximum message identifier length.</summary>
    public int MaximumMessageIdLength { get; set; } = 256;

    /// <summary>Maximum logical message-type length.</summary>
    public int MaximumMessageTypeLength { get; set; } = 128;

    /// <summary>Maximum destination or subscription name length.</summary>
    public int MaximumDestinationNameLength { get; set; } = 128;

    /// <summary>Maximum header-name length.</summary>
    public int MaximumHeaderNameLength { get; set; } = 128;

    /// <summary>Maximum header-value length.</summary>
    public int MaximumHeaderValueLength { get; set; } = 2048;

    /// <summary>Bounded timeout applied around one publish call.</summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Grace period allowed for active consumers during shutdown.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Optional trusted deployment prefix used by the default topology strategy.</summary>
    public string? EnvironmentPrefix { get; set; }

    /// <summary>Additional application-defined headers that may be propagated.</summary>
    public ISet<string> AdditionalAllowedHeaders { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    internal void Validate()
    {
        if (MaximumConcurrentMessages is <= 0 or > 256)
            throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentMessages), "MaximumConcurrentMessages must be between 1 and 256.");
        if (MaximumBufferedMessages is <= 0 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumBufferedMessages), "MaximumBufferedMessages must be between 1 and 1024.");
        if (MaximumPayloadBytes is <= 0 or > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes), "MaximumPayloadBytes must be between 1 byte and 64 MiB.");
        if (MaximumHeaderBytes is <= 0 or > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumHeaderBytes), "MaximumHeaderBytes must be between 1 byte and 1 MiB.");
        if (MaximumHeaderCount is <= 0 or > 256)
            throw new ArgumentOutOfRangeException(nameof(MaximumHeaderCount), "MaximumHeaderCount must be between 1 and 256.");
        ValidateLength(MaximumMessageIdLength, nameof(MaximumMessageIdLength), 32, 1024);
        ValidateLength(MaximumMessageTypeLength, nameof(MaximumMessageTypeLength), 16, 512);
        ValidateLength(MaximumDestinationNameLength, nameof(MaximumDestinationNameLength), 16, 512);
        ValidateLength(MaximumHeaderNameLength, nameof(MaximumHeaderNameLength), 16, 256);
        ValidateLength(MaximumHeaderValueLength, nameof(MaximumHeaderValueLength), 64, 8192);
        ValidateTimeout(PublishTimeout, nameof(PublishTimeout));
        ValidateTimeout(ShutdownTimeout, nameof(ShutdownTimeout));

        if (EnvironmentPrefix is not null)
        {
            string validationValue = EnvironmentPrefix.TrimEnd('-', '.', '_');
            if (validationValue.Length == 0 || EnvironmentPrefix.Any(char.IsWhiteSpace))
                throw new ArgumentException("EnvironmentPrefix must contain a bounded non-whitespace prefix.", nameof(EnvironmentPrefix));
            MessagingValidation.ValidateTopologyName(validationValue, nameof(EnvironmentPrefix), MaximumDestinationNameLength);
        }

        foreach (string header in AdditionalAllowedHeaders)
        {
            MessagingValidation.ValidateHeaderName(header, nameof(AdditionalAllowedHeaders), MaximumHeaderNameLength);
            if (MessagingHeaderPolicy.IsForbiddenHeader(header))
                throw new ArgumentException($"Forbidden header '{header}' cannot be added to the messaging allowlist.", nameof(AdditionalAllowedHeaders));
        }
    }

    private static void ValidateLength(int value, string name, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, $"{name} must be between {minimum} and {maximum}.");
    }

    private static void ValidateTimeout(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromSeconds(120))
            throw new ArgumentOutOfRangeException(name, $"{name} must be greater than zero and no greater than 120 seconds.");
    }
}
