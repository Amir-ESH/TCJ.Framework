using System.Globalization;

namespace TCJ.Messaging.Sagas;

/// <summary>A bounded deterministic correlation key supported by the Saga runtime.</summary>
public readonly record struct SagaCorrelationKey
{
    private const int MaximumStringLength = 256;
    private readonly string _canonicalValue;

    private SagaCorrelationKey(string canonicalValue)
    {
        _canonicalValue = canonicalValue;
    }

    /// <summary>Creates a case-sensitive ordinal string correlation key without implicit trimming.</summary>
    /// <param name="value">Source string correlation value.</param>
    /// <returns>A validated deterministic Saga correlation key.</returns>
    public static SagaCorrelationKey From(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (value.Length > MaximumStringLength)
            throw new ArgumentOutOfRangeException(nameof(value), $"Saga correlation string cannot exceed {MaximumStringLength} characters.");
        return new SagaCorrelationKey(value);
    }

    /// <summary>Creates a GUID correlation key using the canonical lowercase D representation.</summary>
    /// <param name="value">Source GUID correlation value.</param>
    /// <returns>A validated deterministic Saga correlation key.</returns>
    public static SagaCorrelationKey From(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(value), "Saga correlation GUID cannot be empty.");
        return new SagaCorrelationKey(value.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant());
    }

    /// <summary>Creates a signed 64-bit integer correlation key.</summary>
    /// <param name="value">Source 64-bit integer correlation value.</param>
    /// <returns>A validated deterministic Saga correlation key.</returns>
    public static SagaCorrelationKey From(long value) => new(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Creates a signed 32-bit integer correlation key.</summary>
    /// <param name="value">Source 32-bit integer correlation value.</param>
    /// <returns>A validated deterministic Saga correlation key.</returns>
    public static SagaCorrelationKey From(int value) => new(value.ToString(CultureInfo.InvariantCulture));

    internal string CanonicalValue => _canonicalValue ?? throw new InvalidOperationException("Saga correlation key is not initialized.");

    /// <inheritdoc />
    public override string ToString() => "[redacted-correlation]";
}
