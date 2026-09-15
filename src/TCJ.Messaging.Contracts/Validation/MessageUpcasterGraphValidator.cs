using TCJ.Messaging.Serialization;

namespace TCJ.Messaging.Contracts;

/// <summary>Result of upcaster graph or representative-path validation.</summary>
public sealed class MessageUpcasterValidationResult
{
    /// <summary>Gets whether the graph/path is valid.</summary>
    public required bool IsValid { get; init; }

    /// <summary>Gets deterministic validation errors.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>Gets the final upcast payload for a successfully validated representative path.</summary>
    public ReadOnlyMemory<byte> OutputPayload { get; init; }
}

/// <summary>Validates existing <see cref="IMessageUpcaster"/> graphs without introducing a second upcaster abstraction.</summary>
public sealed class MessageUpcasterGraphValidator
{
    /// <summary>Validates all transitions for positive versions, strict advancement, duplicate sources, and cycles.</summary>
    /// <param name="upcasters">Existing TCJ runtime upcasters.</param>
    /// <returns>Graph validation result.</returns>
    public MessageUpcasterValidationResult Validate(IEnumerable<IMessageUpcaster> upcasters)
    {
        ArgumentNullException.ThrowIfNull(upcasters);
        var errors = new List<string>();
        var transitions = new Dictionary<(string Type, int Source), IMessageUpcaster>();
        foreach (IMessageUpcaster upcaster in upcasters)
        {
            if (upcaster is null)
            {
                errors.Add("Upcaster collection contains null.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(upcaster.MessageType) || upcaster.MessageType.Length > 128)
                errors.Add("Upcaster message type is missing or exceeds 128 characters.");
            if (upcaster.SourceVersion <= 0 || upcaster.TargetVersion <= 0)
                errors.Add($"Upcaster '{upcaster.GetType().FullName}' uses a non-positive version.");
            if (upcaster.TargetVersion <= upcaster.SourceVersion)
                errors.Add($"Upcaster '{upcaster.GetType().FullName}' must strictly advance its message version.");
            if (!transitions.TryAdd((upcaster.MessageType, upcaster.SourceVersion), upcaster))
                errors.Add($"Duplicate upcaster source transition for '{upcaster.MessageType}' v{upcaster.SourceVersion}.");
        }

        foreach ((var key, _) in transitions)
        {
            var visited = new HashSet<int>();
            int version = key.Source;
            while (transitions.TryGetValue((key.Type, version), out IMessageUpcaster? next))
            {
                if (!visited.Add(version))
                {
                    errors.Add($"Upcaster graph contains a cycle for '{key.Type}' at version {version}.");
                    break;
                }
                version = next.TargetVersion;
            }
        }

        return Result(errors, default);
    }

    /// <summary>Executes and validates one representative old payload through the required existing upcaster path.</summary>
    /// <param name="upcasters">Existing TCJ runtime upcasters.</param>
    /// <param name="messageType">Logical message type.</param>
    /// <param name="sourceVersion">Example source version.</param>
    /// <param name="targetContract">Registered runtime target contract.</param>
    /// <param name="sourcePayload">Representative old-version payload.</param>
    /// <param name="targetSchema">Generated target schema.</param>
    /// <param name="maximumPayloadBytes">Maximum payload size permitted after each transition.</param>
    /// <returns>Validation result and final payload when successful.</returns>
    public MessageUpcasterValidationResult ValidateRepresentativePath(
        IEnumerable<IMessageUpcaster> upcasters,
        string messageType,
        int sourceVersion,
        MessagingMessageContract targetContract,
        ReadOnlyMemory<byte> sourcePayload,
        GeneratedMessageContractSchema targetSchema,
        int maximumPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(upcasters);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentNullException.ThrowIfNull(targetContract);
        ArgumentNullException.ThrowIfNull(targetSchema);
        if (sourceVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceVersion));
        if (maximumPayloadBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        if (!string.Equals(messageType, targetContract.MessageType, StringComparison.Ordinal))
            throw new ArgumentException("Target runtime contract message type does not match the requested upcaster path.", nameof(messageType));

        IMessageUpcaster[] all = upcasters.ToArray();
        MessageUpcasterValidationResult graph = Validate(all);
        if (!graph.IsValid)
            return graph;

        var transitions = all.ToDictionary(static upcaster => (upcaster.MessageType, upcaster.SourceVersion));
        var errors = new List<string>();
        if (sourcePayload.Length == 0 || sourcePayload.Length > maximumPayloadBytes)
        {
            errors.Add("Source upcaster payload is empty or exceeds the configured payload bound.");
            return Result(errors, sourcePayload);
        }

        ReadOnlyMemory<byte> current = sourcePayload;
        int version = sourceVersion;
        while (version < targetContract.MessageVersion)
        {
            if (!transitions.TryGetValue((messageType, version), out IMessageUpcaster? upcaster))
            {
                errors.Add($"Required upcaster path is missing for '{messageType}' v{version} -> v{targetContract.MessageVersion}.");
                break;
            }
            if (upcaster.TargetVersion > targetContract.MessageVersion)
            {
                errors.Add($"Upcaster path for '{messageType}' advances beyond target version {targetContract.MessageVersion}.");
                break;
            }
            try
            {
                current = upcaster.Upcast(current);
            }
            catch (Exception exception)
            {
                errors.Add($"Upcaster '{upcaster.GetType().FullName}' failed with {exception.GetType().Name}.");
                break;
            }
            if (current.Length > maximumPayloadBytes)
            {
                errors.Add("Upcaster output exceeds the configured payload bound.");
                break;
            }
            version = upcaster.TargetVersion;
        }

        if (errors.Count == 0 && version == targetContract.MessageVersion)
        {
            var exampleValidator = new MessageContractExampleValidator();
            MessageContractExampleValidationResult example = exampleValidator.Validate(targetContract, targetSchema, current, maximumPayloadBytes: maximumPayloadBytes);
            if (!example.IsValid)
                errors.AddRange(example.Errors.Select(static error => "Target upcast payload: " + error));
        }

        return Result(errors, current);
    }

    private static MessageUpcasterValidationResult Result(List<string> errors, ReadOnlyMemory<byte> output) => new()
    {
        IsValid = errors.Count == 0,
        Errors = errors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        OutputPayload = output
    };
}
