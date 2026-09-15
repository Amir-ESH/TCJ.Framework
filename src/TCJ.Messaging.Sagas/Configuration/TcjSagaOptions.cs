namespace TCJ.Messaging.Sagas.Configuration;

/// <summary>Bounded operational defaults for durable Saga orchestration.</summary>
public sealed class TcjSagaOptions
{
    /// <summary>Maximum serialized application-state payload size. Default is 256 KiB.</summary>
    public int MaximumStatePayloadBytes { get; set; } = 256 * 1024;
    /// <summary>Maximum timer claims per processor iteration.</summary>
    public int TimerBatchSize { get; set; } = 20;
    /// <summary>Lease duration used by provider-specific timer claiming.</summary>
    public TimeSpan TimerLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Maximum timeout attempts before a timer is durably failed.</summary>
    public int MaxTimerAttempts { get; set; } = 5;
    /// <summary>Base delay for bounded timer retries.</summary>
    public TimeSpan TimerRetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Maximum explicit compensation attempts.</summary>
    public int MaxCompensationAttempts { get; set; } = 5;
    /// <summary>Base delay for bounded compensation retries.</summary>
    public TimeSpan CompensationRetryBaseDelay { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Retention period for terminal Saga instances. Zero disables automatic cleanup.</summary>
    public TimeSpan TerminalRetentionPeriod { get; set; } = TimeSpan.FromDays(30);
    /// <summary>Maximum terminal Saga instances removed per cleanup call.</summary>
    public int CleanupBatchSize { get; set; } = 100;

    /// <summary>Validates all bounded options.</summary>
    public void Validate()
    {
        if (MaximumStatePayloadBytes is < 1024 or > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumStatePayloadBytes), "Saga state payload limit must be between 1 KiB and 1 MiB.");
        if (TimerBatchSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(TimerBatchSize));
        ValidateDuration(TimerLeaseDuration, nameof(TimerLeaseDuration), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10));
        if (MaxTimerAttempts is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(MaxTimerAttempts));
        ValidateDuration(TimerRetryBaseDelay, nameof(TimerRetryBaseDelay), TimeSpan.FromMilliseconds(100), TimeSpan.FromMinutes(10));
        if (MaxCompensationAttempts is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(MaxCompensationAttempts));
        ValidateDuration(CompensationRetryBaseDelay, nameof(CompensationRetryBaseDelay), TimeSpan.FromMilliseconds(100), TimeSpan.FromMinutes(30));
        if (TerminalRetentionPeriod < TimeSpan.Zero || TerminalRetentionPeriod > TimeSpan.FromDays(3650))
            throw new ArgumentOutOfRangeException(nameof(TerminalRetentionPeriod));
        if (CleanupBatchSize is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(CleanupBatchSize));
    }

    private static void ValidateDuration(TimeSpan value, string name, TimeSpan minimum, TimeSpan maximum)
    {
        if (value < minimum || value > maximum) throw new ArgumentOutOfRangeException(name);
    }
}
