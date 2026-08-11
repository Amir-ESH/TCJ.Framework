using System.Text.Json;

namespace TCJ.Core.Outbox;

/// <summary>
/// Configures bounded transactional-outbox persistence and processing behavior.
/// </summary>
public sealed class TcjOutboxOptions
{
    /// <summary>Creates production-safe transactional-outbox defaults.</summary>
    public TcjOutboxOptions()
    {
        JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        BatchSize = 100;
        PollingInterval = TimeSpan.FromSeconds(1);
        LockDuration = TimeSpan.FromSeconds(30);
        MaxRetryAttempts = 10;
        BaseRetryDelay = TimeSpan.FromSeconds(1);
        MaxRetryDelay = TimeSpan.FromMinutes(5);
        UseJitter = true;
        RetentionPeriod = TimeSpan.FromDays(7);
        CleanupBatchSize = 500;
        CleanupInterval = TimeSpan.FromHours(1);
        MaximumStoredErrorLength = 1024;
        BacklogUnhealthyAge = TimeSpan.FromMinutes(5);
        DeadLetterUnhealthyThreshold = 1;
    }

    /// <summary>Gets configurable JSON settings used by the default System.Text.Json serializer.</summary>
    /// <remarks>The default configuration does not enable polymorphic type metadata or arbitrary type activation.</remarks>
    public JsonSerializerOptions JsonSerializerOptions { get; }

    /// <summary>Maximum number of messages claimed by one processing batch. Default: 100.</summary>
    public int BatchSize { get; set; }

    /// <summary>Delay between hosted-service polls when work is not immediately available. Default: one second.</summary>
    public TimeSpan PollingInterval { get; set; }

    /// <summary>Maximum duration of a processing claim before another worker may reclaim it. Default: 30 seconds.</summary>
    public TimeSpan LockDuration { get; set; }

    /// <summary>Maximum retry count after the initial delivery attempt. Default: 10.</summary>
    public int MaxRetryAttempts { get; set; }

    /// <summary>Base delay used by bounded exponential retry scheduling. Default: one second.</summary>
    public TimeSpan BaseRetryDelay { get; set; }

    /// <summary>Maximum retry delay. Default: five minutes.</summary>
    public TimeSpan MaxRetryDelay { get; set; }

    /// <summary>Whether bounded deterministic jitter is applied to retry schedules. Default: true.</summary>
    public bool UseJitter { get; set; }

    /// <summary>
    /// Retention for successfully processed records. Set to <see cref="TimeSpan.Zero"/> to disable cleanup.
    /// Default: seven days.
    /// </summary>
    public TimeSpan RetentionPeriod { get; set; }

    /// <summary>Maximum number of records deleted by one cleanup operation. Default: 500.</summary>
    public int CleanupBatchSize { get; set; }

    /// <summary>Hosted cleanup cadence when retention is enabled. Default: one hour.</summary>
    public TimeSpan CleanupInterval { get; set; }

    /// <summary>Maximum persisted error-message length. Default: 1024 characters.</summary>
    public int MaximumStoredErrorLength { get; set; }

    /// <summary>Readiness threshold for the age of the oldest pending message. Default: five minutes.</summary>
    public TimeSpan BacklogUnhealthyAge { get; set; }

    /// <summary>Readiness threshold for dead-lettered messages. Zero disables count-based failure. Default: 1.</summary>
    public int DeadLetterUnhealthyThreshold { get; set; }

    /// <summary>Validates all bounded outbox settings.</summary>
    public void Validate()
    {
        if (BatchSize is <= 0 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(BatchSize), "Outbox batch size must be between 1 and 1000.");
        }

        if (PollingInterval <= TimeSpan.Zero || PollingInterval > TimeSpan.FromSeconds(60))
        {
            throw new ArgumentOutOfRangeException(nameof(PollingInterval), "Outbox polling interval must be greater than zero and no more than 60 seconds.");
        }

        if (LockDuration <= TimeSpan.Zero || LockDuration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(LockDuration), "Outbox lock duration must be greater than zero and no more than five minutes.");
        }

        if (MaxRetryAttempts is < 0 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetryAttempts), "Outbox retry count must be between 0 and 20.");
        }

        if (BaseRetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(BaseRetryDelay), "Outbox retry base delay must be greater than zero to prevent immediate retry loops.");
        }

        if (MaxRetryDelay <= TimeSpan.Zero || MaxRetryDelay > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetryDelay), "Outbox maximum retry delay must be greater than zero and no more than 30 minutes.");
        }

        if (BaseRetryDelay > MaxRetryDelay)
        {
            throw new ArgumentException("Outbox retry base delay cannot exceed the maximum retry delay.", nameof(BaseRetryDelay));
        }

        if (RetentionPeriod < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RetentionPeriod), "Outbox retention period cannot be negative.");
        }

        if (CleanupBatchSize is <= 0 or > 5000)
        {
            throw new ArgumentOutOfRangeException(nameof(CleanupBatchSize), "Outbox cleanup batch size must be between 1 and 5000.");
        }

        if (CleanupInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CleanupInterval), "Outbox cleanup interval must be greater than zero.");
        }

        if (MaximumStoredErrorLength is <= 0 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumStoredErrorLength), "Stored outbox error length must be between 1 and 4096 characters.");
        }

        if (BacklogUnhealthyAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(BacklogUnhealthyAge), "Outbox backlog age threshold must be greater than zero.");
        }

        if (DeadLetterUnhealthyThreshold < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DeadLetterUnhealthyThreshold), "Outbox dead-letter threshold cannot be negative.");
        }
    }
}
