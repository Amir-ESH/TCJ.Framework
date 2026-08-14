using System.Collections.ObjectModel;

namespace TCJ.Core.Results;

/// <summary>
/// Represents an immutable, structured failure reason.
/// </summary>
public sealed class ResultError : IEquatable<ResultError>
{
    private readonly IReadOnlyDictionary<string, object?> _metadata;

    /// <summary>
    /// Initializes a new result error.
    /// </summary>
    public ResultError(string code,
                       string message,
                       ResultErrorType type = ResultErrorType.Failure)
        : this(code, message, type, metadata: new Dictionary<string, object?>())
    {
    }

    private ResultError(string code,
                        string message,
                        ResultErrorType type,
                        IDictionary<string, object?> metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(metadata);

        Code = code;
        Message = message;
        Type = type;
        _metadata = new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(metadata, StringComparer.Ordinal));
    }

    /// <summary>
    /// Gets the stable error code used for programmatic handling.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Gets the human-readable error message.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Gets the semantic error category.
    /// </summary>
    public ResultErrorType Type { get; }

    /// <summary>
    /// Gets optional structured context for the error.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Metadata => _metadata;

    /// <summary>
    /// Returns a new error containing the supplied metadata value.
    /// </summary>
    public ResultError WithMetadata(string key, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var metadata = _metadata.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);

        metadata[key] = value;

        return new ResultError(Code, Message, Type, metadata);
    }

    /// <inheritdoc />
    public bool Equals(ResultError? other)
        => other is not null
        && Type == other.Type
        && StringComparer.Ordinal.Equals(Code, other.Code)
        && StringComparer.Ordinal.Equals(Message, other.Message);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ResultError other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(Type, StringComparer.Ordinal.GetHashCode(Code), StringComparer.Ordinal.GetHashCode(Message));

    /// <inheritdoc />
    public override string ToString() => $"[{Code}] {Message}";

    public static bool operator ==(ResultError? left, ResultError? right)
        => EqualityComparer<ResultError>.Default.Equals(left, right);

    public static bool operator !=(ResultError? left, ResultError? right)
        => !EqualityComparer<ResultError>.Default.Equals(left, right);
}
