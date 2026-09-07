using Azure.Messaging.ServiceBus;
using TCJ.Messaging.AzureServiceBus.Topology;

namespace TCJ.Messaging.AzureServiceBus.Configuration;

/// <summary>Controls Azure Service Bus adapter behavior without exposing broker SDK option types.</summary>
public sealed class TcjAzureServiceBusOptions
{
    /// <summary>Optional Service Bus namespace used with token credentials.</summary>
    public string? FullyQualifiedNamespace { get; set; }
    /// <summary>Optional Service Bus connection string. Prefer token credentials for production.</summary>
    public string? ConnectionString { get; set; }
    /// <summary>Optional separate management endpoint connection string, primarily for the local emulator.</summary>
    public string? ManagementConnectionString { get; set; }
    /// <summary>Maximum number of prefetched messages per receiver.</summary>
    public int PrefetchCount { get; set; } = 32;
    /// <summary>Maximum concurrent non-session message handlers.</summary>
    public int MaximumConcurrentMessages { get; set; } = 8;
    /// <summary>Maximum concurrently accepted Service Bus sessions.</summary>
    public int MaximumConcurrentSessions { get; set; } = 4;
    /// <summary>Maximum calls per session. TCJ requires one to preserve strict per-session ordering.</summary>
    public int MaximumConcurrentCallsPerSession { get; set; } = 1;
    /// <summary>Maximum Azure SDK retry attempts for one broker operation.</summary>
    public int MaximumRetries { get; set; } = 3;
    /// <summary>Initial Azure SDK retry delay.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(800);
    /// <summary>Maximum Azure SDK retry delay.</summary>
    public TimeSpan MaximumRetryDelay { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Bounded Azure SDK operation timeout.</summary>
    public TimeSpan TryTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Azure SDK retry mode represented by an adapter-owned enum.</summary>
    public AzureServiceBusRetryMode RetryMode { get; set; } = AzureServiceBusRetryMode.Exponential;
    /// <summary>Maximum duration for TCJ message/session lock renewal.</summary>
    public TimeSpan MaxAutoLockRenewalDuration { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Bounded graceful shutdown time.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Receive polling duration used to observe shutdown cancellation promptly.</summary>
    public TimeSpan ReceiveWaitTime { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Maximum number of cached destination senders.</summary>
    public int MaximumSenderCacheSize { get; set; } = 128;
    /// <summary>Optional configured queue or topic used by readiness to open a sender link without publishing a message.</summary>
    public string? ReadinessDestination { get; set; }
    /// <summary>Optional adapter policy ceiling for per-message TTL.</summary>
    public TimeSpan? MaximumMessageTimeToLive { get; set; }
    /// <summary>Must remain false. TCJ Inbox controls completion after transactional processing.</summary>
    public bool AutoCompleteMessages { get; set; }
    /// <summary>Default delay used by scheduled-clone retry when the Inbox does not provide an explicit delay.</summary>
    public TimeSpan DefaultRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Default settlement strategy used for Retry outcomes.</summary>
    public AzureServiceBusRetrySettlementStrategy RetrySettlementStrategy { get; set; } = AzureServiceBusRetrySettlementStrategy.ScheduledClone;
    /// <summary>Explicit topology ownership mode.</summary>
    public AzureServiceBusTopologyMode TopologyMode { get; set; } = AzureServiceBusTopologyMode.Disabled;
    /// <summary>Declared queue/topic/subscription topology.</summary>
    public AzureServiceBusTopologyOptions Topology { get; } = new();

    internal void Validate(bool hasTokenCredential)
    {
        bool hasNamespace = !string.IsNullOrWhiteSpace(FullyQualifiedNamespace);
        bool hasConnectionString = !string.IsNullOrWhiteSpace(ConnectionString);
        if (hasTokenCredential)
        {
            if (!hasNamespace || hasConnectionString)
                throw new ArgumentException("Token-credential registration requires FullyQualifiedNamespace and must not also configure ConnectionString.");
        }
        else if (hasNamespace == hasConnectionString)
        {
            throw new ArgumentException("Configure exactly one of FullyQualifiedNamespace or ConnectionString.");
        }
        if (hasNamespace) AzureServiceBusValidation.ValidateNamespace(FullyQualifiedNamespace!);
        if (hasConnectionString) AzureServiceBusValidation.ValidateConnectionString(ConnectionString!);
        if (ManagementConnectionString is not null) AzureServiceBusValidation.ValidateConnectionString(ManagementConnectionString);
        if (PrefetchCount is < 0 or > 10000) throw new ArgumentOutOfRangeException(nameof(PrefetchCount), "PrefetchCount must be between 0 and 10000.");
        if (MaximumConcurrentMessages is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentMessages), "MaximumConcurrentMessages must be between 1 and 256.");
        if (MaximumConcurrentSessions is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentSessions), "MaximumConcurrentSessions must be between 1 and 128.");
        if (MaximumConcurrentCallsPerSession != 1) throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentCallsPerSession), "MaximumConcurrentCallsPerSession must be 1 so the adapter can guarantee per-session ordering.");
        if (MaximumRetries is < 0 or > 10) throw new ArgumentOutOfRangeException(nameof(MaximumRetries), "MaximumRetries must be between 0 and 10; durable retry belongs to TCJ Outbox.");
        AzureServiceBusValidation.ValidateNonNegativeDelay(RetryDelay, nameof(RetryDelay), TimeSpan.FromMinutes(1));
        AzureServiceBusValidation.ValidateNonNegativeDelay(MaximumRetryDelay, nameof(MaximumRetryDelay), TimeSpan.FromMinutes(2));
        if (MaximumRetryDelay < RetryDelay) throw new ArgumentException("MaximumRetryDelay cannot be less than RetryDelay.", nameof(MaximumRetryDelay));
        AzureServiceBusValidation.ValidatePositiveTimeout(TryTimeout, nameof(TryTimeout), TimeSpan.FromSeconds(120));
        AzureServiceBusValidation.ValidatePositiveTimeout(MaxAutoLockRenewalDuration, nameof(MaxAutoLockRenewalDuration), TimeSpan.FromMinutes(30));
        AzureServiceBusValidation.ValidatePositiveTimeout(ShutdownTimeout, nameof(ShutdownTimeout), TimeSpan.FromMinutes(2));
        AzureServiceBusValidation.ValidatePositiveTimeout(ReceiveWaitTime, nameof(ReceiveWaitTime), TimeSpan.FromSeconds(30));
        if (MaximumSenderCacheSize is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(MaximumSenderCacheSize), "MaximumSenderCacheSize must be between 1 and 1024.");
        if (ReadinessDestination is not null) AzureServiceBusValidation.ValidateEntityName(ReadinessDestination, nameof(ReadinessDestination));
        if (MaximumMessageTimeToLive is { } ttl) AzureServiceBusValidation.ValidatePositiveTimeout(ttl, nameof(MaximumMessageTimeToLive), TimeSpan.FromDays(365));
        AzureServiceBusValidation.ValidatePositiveTimeout(DefaultRetryDelay, nameof(DefaultRetryDelay), TimeSpan.FromHours(1));
        if (AutoCompleteMessages) throw new ArgumentException("AutoCompleteMessages must remain false because Inbox commit owns completion.", nameof(AutoCompleteMessages));
        if (!Enum.IsDefined(RetryMode)) throw new ArgumentOutOfRangeException(nameof(RetryMode));
        if (!Enum.IsDefined(RetrySettlementStrategy)) throw new ArgumentOutOfRangeException(nameof(RetrySettlementStrategy));
        if (!Enum.IsDefined(TopologyMode)) throw new ArgumentOutOfRangeException(nameof(TopologyMode));
        Topology.Validate();
    }
}

/// <summary>Adapter-owned Azure SDK retry mode.</summary>
public enum AzureServiceBusRetryMode { Fixed = 0, Exponential = 1 }

/// <summary>How TCJ maps a transport-neutral Retry settlement to Azure Service Bus.</summary>
public enum AzureServiceBusRetrySettlementStrategy
{
    /// <summary>Release the lock for immediate broker redelivery.</summary>
    Abandon = 0,
    /// <summary>Schedule a retry clone for delayed retries and complete the original only after scheduling succeeds.</summary>
    ScheduledClone = 1,
    /// <summary>Defer the delivery. Retrieval by sequence number remains explicit.</summary>
    Defer = 2
}

internal static class AzureServiceBusValidation
{
    internal static void ValidateNamespace(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 255 || value.Any(char.IsControl)) throw new ArgumentException("FullyQualifiedNamespace is invalid.", nameof(value));
        if (!Uri.TryCreate($"https://{value}", UriKind.Absolute, out Uri? uri) || !string.Equals(uri.Host, value, StringComparison.OrdinalIgnoreCase) || value.Contains('/'))
            throw new ArgumentException("FullyQualifiedNamespace must be a valid host name without a scheme or path.", nameof(value));
    }

    internal static void ValidateConnectionString(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 4096 || value.Any(static c => c is '\r' or '\n' or '\0')) throw new ArgumentException("ConnectionString is malformed.", nameof(value));
        try
        {
            ServiceBusConnectionStringProperties properties = ServiceBusConnectionStringProperties.Parse(value);
            if (string.IsNullOrWhiteSpace(properties.FullyQualifiedNamespace)) throw new FormatException();
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("ConnectionString is not a valid Azure Service Bus connection string.", nameof(value), exception);
        }
    }

    internal static void ValidatePositiveTimeout(TimeSpan value, string parameterName, TimeSpan maximum)
    {
        if (value <= TimeSpan.Zero || value > maximum) throw new ArgumentOutOfRangeException(parameterName, $"{parameterName} must be greater than zero and no greater than {maximum}.");
    }

    internal static void ValidateNonNegativeDelay(TimeSpan value, string parameterName, TimeSpan maximum)
    {
        if (value < TimeSpan.Zero || value > maximum) throw new ArgumentOutOfRangeException(parameterName, $"{parameterName} must be non-negative and no greater than {maximum}.");
    }

    internal static void ValidateEntityName(string value, string parameterName, int maximumLength = 260)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || value.Any(char.IsControl) || value.Any(char.IsWhiteSpace))
            throw new ArgumentException($"{parameterName} must be {maximumLength} characters or fewer and contain no whitespace/control characters.", parameterName);
        if (value.StartsWith('$')) throw new ArgumentException($"{parameterName} cannot use a broker-reserved '$' prefix.", parameterName);
    }

    internal static string BoundSafeText(string? value, int maximumLength, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        string safe = new(value.Where(static c => !char.IsControl(c)).Take(maximumLength).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? fallback : safe;
    }
}
