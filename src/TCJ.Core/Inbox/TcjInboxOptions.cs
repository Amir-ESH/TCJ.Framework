using System.Text.Json;

namespace TCJ.Core.Inbox;

/// <summary>Configures one consumer-scoped transactional Inbox boundary.</summary>
public sealed class TcjInboxOptions
{
    private static readonly HashSet<string> ForbiddenSensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "cookie", "set-cookie", "api-key", "access-token", "refresh-token"
    };

    /// <summary>Creates bounded production-safe defaults. <see cref="ConsumerName"/> must be configured explicitly.</summary>
    public TcjInboxOptions()
    {
        JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        HeaderAllowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "correlation-id", "causation-id", "content-type", "traceparent", "tracestate"
        };
    }

    /// <summary>Stable required logical consumer boundary. Changing it changes the idempotency scope.</summary>
    public string ConsumerName { get; set; } = string.Empty;
    /// <summary>Configured processing mode. Default: <see cref="InboxProcessingMode.Inline"/>.</summary>
    public InboxProcessingMode ProcessingMode { get; set; } = InboxProcessingMode.Inline;
    /// <summary>Maximum deferred claim size. Default: 100.</summary>
    public int BatchSize { get; set; } = 100;
    /// <summary>Maximum retries after the initial attempt. Default: 10.</summary>
    public int MaxRetryAttempts { get; set; } = 10;
    /// <summary>Deferred processor polling interval. Default: one second.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>Maximum duration of a deferred processing lease. Default: 30 seconds.</summary>
    public TimeSpan LockDuration { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Base bounded exponential retry delay. Default: one second.</summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>Maximum retry delay. Default: five minutes.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Whether bounded deterministic jitter is added to retries. Default: true.</summary>
    public bool UseJitter { get; set; } = true;
    /// <summary>Retention for processed records; zero disables cleanup. Default: 14 days.</summary>
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(14);
    /// <summary>Maximum cleanup batch size. Default: 500.</summary>
    public int CleanupBatchSize { get; set; } = 500;
    /// <summary>Hosted cleanup cadence when retention is enabled. Default: one hour.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);
    /// <summary>Whether serialized payloads are retained. Deferred processing requires this to be true. Default: true.</summary>
    public bool StorePayload { get; set; } = true;
    /// <summary>Maximum accepted UTF-8 payload size. Default: 1 MiB.</summary>
    public int MaximumPayloadBytes { get; set; } = 1024 * 1024;
    /// <summary>Maximum persisted safe error-summary length. Default: 1024.</summary>
    public int MaximumStoredErrorLength { get; set; } = 1024;
    /// <summary>Readiness threshold for the oldest pending message. Default: five minutes.</summary>
    public TimeSpan BacklogUnhealthyAge { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Dead-letter count at which readiness becomes degraded; zero disables count-based degradation. Default: 1.</summary>
    public int DeadLetterUnhealthyThreshold { get; set; } = 1;
    /// <summary>Gets the allowlist of headers that may be persisted. Authorization and credential headers are not included by default.</summary>
    public ISet<string> HeaderAllowlist { get; }
    /// <summary>Gets configurable JSON options used only for explicitly registered CLR message types.</summary>
    public JsonSerializerOptions JsonSerializerOptions { get; }

    /// <summary>Validates bounded Inbox configuration and the required consumer contract.</summary>
    public void Validate()
    {
        ValidateContractName(ConsumerName, nameof(ConsumerName), 128);
        if (BatchSize is <= 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(BatchSize), "Inbox batch size must be between 1 and 1000.");
        if (MaxRetryAttempts is < 0 or > 20) throw new ArgumentOutOfRangeException(nameof(MaxRetryAttempts), "Inbox retry count must be between 0 and 20.");
        if (PollingInterval <= TimeSpan.Zero || PollingInterval > TimeSpan.FromSeconds(60)) throw new ArgumentOutOfRangeException(nameof(PollingInterval), "Inbox polling interval must be greater than zero and no more than 60 seconds.");
        if (LockDuration <= TimeSpan.Zero || LockDuration > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(LockDuration), "Inbox lock duration must be greater than zero and no more than five minutes.");
        if (BaseRetryDelay <= TimeSpan.Zero || MaxRetryDelay <= TimeSpan.Zero || BaseRetryDelay > MaxRetryDelay || MaxRetryDelay > TimeSpan.FromMinutes(30)) throw new ArgumentException("Inbox retry delays must be positive, bounded, and ordered.");
        if (RetentionPeriod < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RetentionPeriod), "Inbox retention cannot be negative.");
        if (CleanupBatchSize is <= 0 or > 5000) throw new ArgumentOutOfRangeException(nameof(CleanupBatchSize), "Inbox cleanup batch size must be between 1 and 5000.");
        if (CleanupInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(CleanupInterval), "Inbox cleanup interval must be greater than zero.");
        if (MaximumPayloadBytes is <= 0 or > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes), "Inbox maximum payload size must be between 1 byte and 16 MiB.");
        if (MaximumStoredErrorLength is <= 0 or > 4096) throw new ArgumentOutOfRangeException(nameof(MaximumStoredErrorLength), "Inbox stored error length must be between 1 and 4096 characters.");
        if (BacklogUnhealthyAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(BacklogUnhealthyAge), "Inbox backlog threshold must be greater than zero.");
        if (DeadLetterUnhealthyThreshold < 0) throw new ArgumentOutOfRangeException(nameof(DeadLetterUnhealthyThreshold), "Inbox dead-letter threshold cannot be negative.");
        if (ProcessingMode == InboxProcessingMode.Deferred && !StorePayload) throw new InvalidOperationException("Deferred Inbox processing requires payload retention because the handler runs after transport acknowledgement.");
        if (HeaderAllowlist.Count > 32) throw new InvalidOperationException("Inbox header allowlist cannot contain more than 32 names.");
        foreach (string header in HeaderAllowlist)
        {
            ValidateContractName(header, nameof(HeaderAllowlist), 128);
            if (ForbiddenSensitiveHeaders.Contains(header))
            {
                throw new InvalidOperationException($"Inbox header '{header}' is security-sensitive and cannot be persisted by the Inbox header allowlist.");
            }
        }
    }

    internal static void ValidateContractName(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || value.Any(static c => !(char.IsLetterOrDigit(c) || c is '.' or '-' or '_')))
        {
            throw new ArgumentException($"Inbox {parameterName} must be {maximumLength} characters or fewer and contain only letters, numbers, '.', '-' and '_'.", parameterName);
        }
    }
}
