using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TCJ.Messaging.Contracts;

/// <summary>One conservative compatibility finding.</summary>
/// <param name="Code">Stable machine-readable finding code.</param>
/// <param name="Status">Finding severity.</param>
/// <param name="Path">Schema location associated with the finding.</param>
/// <param name="Message">Bounded human-readable explanation.</param>
public sealed record MessageContractCompatibilityFinding(
    string Code,
    MessageContractCompatibilityStatus Status,
    string Path,
    string Message);

/// <summary>Result of structural schema compatibility analysis.</summary>
public sealed class MessageContractCompatibilityResult
{
    /// <summary>Gets the requested compatibility mode.</summary>
    public required MessageContractCompatibilityMode Mode { get; init; }

    /// <summary>Gets the conservative aggregate result.</summary>
    public required MessageContractCompatibilityStatus Status { get; init; }

    /// <summary>Gets deterministic findings supporting the result.</summary>
    public IReadOnlyList<MessageContractCompatibilityFinding> Findings { get; init; } = [];

    /// <summary>Determines whether the result can pass unattended governance with explicit reviewed exceptions.</summary>
    /// <param name="reviewedExceptions">Reviewed exceptions with bounded justifications.</param>
    /// <returns><see langword="true"/> only for compatible results or fully reviewed review-required findings.</returns>
    public bool IsAccepted(IEnumerable<MessageContractReviewException>? reviewedExceptions = null)
    {
        if (Status == MessageContractCompatibilityStatus.Compatible)
            return true;
        if (Status == MessageContractCompatibilityStatus.Breaking)
            return false;

        var reviewed = (reviewedExceptions ?? [])
            .Where(static item => !string.IsNullOrWhiteSpace(item.Code) &&
                                  !string.IsNullOrWhiteSpace(item.Justification) &&
                                  item.Justification.Length <= 1024)
            .Select(static item => item.Code)
            .ToHashSet(StringComparer.Ordinal);

        return Findings
            .Where(static finding => finding.Status == MessageContractCompatibilityStatus.ReviewRequired)
            .All(finding => reviewed.Contains(finding.Code));
    }
}

/// <summary>Conservative JSON-schema compatibility analyzer using standard Backward/Forward/Full direction terminology.</summary>
public sealed class MessageContractCompatibilityAnalyzer
{
    private static readonly HashSet<string> AnnotationKeywords = new(StringComparer.Ordinal)
    {
        "$schema", "title", "description", "$comment", "default", "examples", "deprecated", "readOnly", "writeOnly"
    };

    private static readonly HashSet<string> SupportedKeywords = new(StringComparer.Ordinal)
    {
        "type", "properties", "required", "additionalProperties", "items", "enum", "const",
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "minLength", "maxLength",
        "minItems", "maxItems", "pattern", "format"
    };

    /// <summary>Analyzes one current schema against the immediately previous published schema.</summary>
    /// <param name="previousSchemaUtf8">Previous published JSON schema.</param>
    /// <param name="currentSchemaUtf8">Current candidate JSON schema.</param>
    /// <param name="mode">Requested compatibility direction.</param>
    /// <returns>Conservative compatibility result.</returns>
    public MessageContractCompatibilityResult Analyze(
        ReadOnlyMemory<byte> previousSchemaUtf8,
        ReadOnlyMemory<byte> currentSchemaUtf8,
        MessageContractCompatibilityMode mode) =>
        Analyze([previousSchemaUtf8], currentSchemaUtf8, mode);

    /// <summary>Analyzes the candidate schema against retained published history. Transitive modes evaluate every retained schema.</summary>
    /// <param name="retainedPublishedSchemas">Published schemas ordered from oldest to newest.</param>
    /// <param name="currentSchemaUtf8">Current candidate JSON schema.</param>
    /// <param name="mode">Requested compatibility mode.</param>
    /// <returns>Conservative aggregate result.</returns>
    public MessageContractCompatibilityResult Analyze(
        IReadOnlyList<ReadOnlyMemory<byte>> retainedPublishedSchemas,
        ReadOnlyMemory<byte> currentSchemaUtf8,
        MessageContractCompatibilityMode mode)
    {
        ArgumentNullException.ThrowIfNull(retainedPublishedSchemas);
        if (mode == MessageContractCompatibilityMode.None || retainedPublishedSchemas.Count == 0)
            return Result(mode, []);

        int startIndex = IsTransitive(mode) ? 0 : retainedPublishedSchemas.Count - 1;
        var findings = new List<MessageContractCompatibilityFinding>();
        for (int index = startIndex; index < retainedPublishedSchemas.Count; index++)
        {
            AnalyzePair(retainedPublishedSchemas[index], currentSchemaUtf8, Normalize(mode), index, findings);
        }

        return Result(mode, findings);
    }

    private static void AnalyzePair(
        ReadOnlyMemory<byte> previousSchemaUtf8,
        ReadOnlyMemory<byte> currentSchemaUtf8,
        MessageContractCompatibilityMode mode,
        int historyIndex,
        List<MessageContractCompatibilityFinding> findings)
    {
        try
        {
            byte[] previousCanonical = MessageContractSchemaGenerator.Canonicalize(previousSchemaUtf8.Span);
            byte[] currentCanonical = MessageContractSchemaGenerator.Canonicalize(currentSchemaUtf8.Span);
            if (previousCanonical.AsSpan().SequenceEqual(currentCanonical))
                return;

            var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 128 };
            using JsonDocument previous = JsonDocument.Parse(previousSchemaUtf8, options);
            using JsonDocument current = JsonDocument.Parse(currentSchemaUtf8, options);
            string prefix = $"history[{historyIndex}]";

            if (mode is MessageContractCompatibilityMode.Backward or MessageContractCompatibilityMode.Full)
                CanReaderAcceptWriter(current.RootElement, previous.RootElement, "$", $"{prefix}:backward", findings);
            if (mode is MessageContractCompatibilityMode.Forward or MessageContractCompatibilityMode.Full)
                CanReaderAcceptWriter(previous.RootElement, current.RootElement, "$", $"{prefix}:forward", findings);
        }
        catch (JsonException exception)
        {
            findings.Add(new MessageContractCompatibilityFinding(
                "SCHEMA_INVALID",
                MessageContractCompatibilityStatus.ReviewRequired,
                $"history[{historyIndex}]",
                Bounded($"Schema input could not be parsed safely: {exception.Message}")));
        }
    }

    private static void CanReaderAcceptWriter(
        JsonElement reader,
        JsonElement writer,
        string path,
        string codePrefix,
        List<MessageContractCompatibilityFinding> findings)
    {
        if (JsonElement.DeepEquals(reader, writer))
            return;

        if (reader.ValueKind is JsonValueKind.True)
            return;
        if (writer.ValueKind is JsonValueKind.False)
            return;
        if (reader.ValueKind is JsonValueKind.False)
        {
            AddBreaking(findings, codePrefix, "READER_REJECTS_ALL", path, "Reader schema rejects all values accepted by the writer schema.");
            return;
        }
        if (writer.ValueKind is JsonValueKind.True)
        {
            AddReview(findings, codePrefix, "WRITER_ACCEPTS_ANY", path, "Writer schema accepts arbitrary JSON; safe containment cannot be proven for the reader schema.");
            return;
        }
        if (reader.ValueKind != JsonValueKind.Object || writer.ValueKind != JsonValueKind.Object)
        {
            AddReview(findings, codePrefix, "UNSUPPORTED_SCHEMA_FORM", path, "Schema form is not an object/boolean JSON Schema that TCJ can safely compare.");
            return;
        }

        if (!ValidateSupportedKeywordShapes(reader, path, codePrefix + ":reader", findings) ||
            !ValidateSupportedKeywordShapes(writer, path, codePrefix + ":writer", findings))
            return;

        CompareUnsupportedKeywords(reader, writer, path, codePrefix, findings);
        CompareTypes(reader, writer, path, codePrefix, findings);
        CompareEnumAndConst(reader, writer, path, codePrefix, findings);
        CompareNumericBounds(reader, writer, path, codePrefix, findings);
        CompareLengthBounds(reader, writer, path, codePrefix, findings);
        ComparePatternAndFormat(reader, writer, path, codePrefix, findings);

        if (MayContainType(writer, "object") && MayContainType(reader, "object"))
            CompareObjects(reader, writer, path, codePrefix, findings);
        if (MayContainType(writer, "array") && MayContainType(reader, "array"))
            CompareArrays(reader, writer, path, codePrefix, findings);
    }

    private static void CompareTypes(JsonElement reader, JsonElement writer, string path, string prefix, List<MessageContractCompatibilityFinding> findings)
    {
        HashSet<string>? readerTypes = ReadTypes(reader);
        HashSet<string>? writerTypes = ReadTypes(writer);
        if (writerTypes is null)
        {
            if (readerTypes is not null)
                AddReview(findings, prefix, "WRITER_TYPE_UNBOUNDED", path, "Writer schema has no bounded type while the reader schema constrains type.");
            return;
        }
        if (readerTypes is null)
            return;

        foreach (string writerType in writerTypes)
        {
            bool accepted = readerTypes.Contains(writerType) ||
                            (string.Equals(writerType, "integer", StringComparison.Ordinal) && readerTypes.Contains("number"));
            if (!accepted)
            {
                AddBreaking(findings, prefix, "TYPE_NARROWED", path,
                    $"Reader types [{string.Join(", ", readerTypes.Order(StringComparer.Ordinal))}] do not accept writer type '{writerType}'.");
            }
        }
    }

    private static void CompareEnumAndConst(JsonElement reader, JsonElement writer, string path, string prefix, List<MessageContractCompatibilityFinding> findings)
    {
        if (writer.TryGetProperty("const", out JsonElement writerConst))
        {
            if (reader.TryGetProperty("const", out JsonElement readerConst) && !JsonElement.DeepEquals(readerConst, writerConst))
                AddBreaking(findings, prefix, "CONST_CHANGED", path, "Reader const value differs from a value the writer can produce.");
            if (reader.TryGetProperty("enum", out JsonElement readerEnum) && !EnumContains(readerEnum, writerConst))
                AddBreaking(findings, prefix, "ENUM_REMOVED_VALUE", path, "Reader enum does not contain the writer const value.");
            return;
        }

        if (writer.TryGetProperty("enum", out JsonElement writerEnum))
        {
            if (reader.TryGetProperty("const", out JsonElement readerConst))
            {
                if (writerEnum.EnumerateArray().Any(value => !JsonElement.DeepEquals(value, readerConst)))
                    AddBreaking(findings, prefix, "ENUM_NARROWED_TO_CONST", path, "Reader const does not accept every value allowed by the writer enum.");
                return;
            }
            if (reader.TryGetProperty("enum", out JsonElement readerEnum))
            {
                foreach (JsonElement value in writerEnum.EnumerateArray())
                {
                    if (!EnumContains(readerEnum, value))
                    {
                        AddBreaking(findings, prefix, "ENUM_REMOVED_VALUE", path, "Reader enum removed a value that the writer schema permits.");
                        break;
                    }
                }
            }
            return;
        }

        if (reader.TryGetProperty("enum", out _) || reader.TryGetProperty("const", out _))
            AddBreaking(findings, prefix, "ENUM_READER_NARROWER", path, "Reader introduces enum/const constraints that the writer schema does not enforce.");
    }

    private static void CompareNumericBounds(JsonElement reader, JsonElement writer, string path, string prefix, List<MessageContractCompatibilityFinding> findings)
    {
        NumericBound? readerMinimum = ReadEffectiveLowerBound(reader);
        NumericBound? writerMinimum = ReadEffectiveLowerBound(writer);
        if (readerMinimum is not null &&
            (writerMinimum is null || readerMinimum.Value.Value > writerMinimum.Value.Value ||
             (readerMinimum.Value.Value == writerMinimum.Value.Value && readerMinimum.Value.Exclusive && !writerMinimum.Value.Exclusive)))
        {
            AddBreaking(findings, prefix, "MINIMUM_NARROWED", path, "Reader numeric lower bound is stricter than the writer constraint.");
        }

        NumericBound? readerMaximum = ReadEffectiveUpperBound(reader);
        NumericBound? writerMaximum = ReadEffectiveUpperBound(writer);
        if (readerMaximum is not null &&
            (writerMaximum is null || readerMaximum.Value.Value < writerMaximum.Value.Value ||
             (readerMaximum.Value.Value == writerMaximum.Value.Value && readerMaximum.Value.Exclusive && !writerMaximum.Value.Exclusive)))
        {
            AddBreaking(findings, prefix, "MAXIMUM_NARROWED", path, "Reader numeric upper bound is stricter than the writer constraint.");
        }
    }

    private static void CompareLengthBounds(JsonElement reader, JsonElement writer, string path, string prefix, List<MessageContractCompatibilityFinding> findings)
    {
        CompareMin("minLength");
        CompareMax("maxLength");
        CompareMin("minItems");
        CompareMax("maxItems");

        void CompareMin(string keyword)
        {
            long? readerValue = ReadInt64(reader, keyword);
            long? writerValue = ReadInt64(writer, keyword);
            if (readerValue is not null && (writerValue is null || readerValue > writerValue))
                AddBreaking(findings, prefix, "MIN_BOUND_NARROWED", path, $"Reader '{keyword}' is stricter than the writer constraint.");
        }

        void CompareMax(string keyword)
        {
            long? readerValue = ReadInt64(reader, keyword);
            long? writerValue = ReadInt64(writer, keyword);
            if (readerValue is not null && (writerValue is null || readerValue < writerValue))
                AddBreaking(findings, prefix, "MAX_BOUND_NARROWED", path, $"Reader '{keyword}' is stricter than the writer constraint.");
        }
    }

    private static void ComparePatternAndFormat(JsonElement reader, JsonElement writer, string path, string prefix, List<MessageContractCompatibilityFinding> findings)
    {
        foreach (string keyword in new[] { "pattern", "format" })
        {
            string? readerValue = ReadString(reader, keyword);
            string? writerValue = ReadString(writer, keyword);
            if (readerValue is null)
                continue;
            if (writerValue is null)
                AddReview(findings, prefix, "TEXT_CONSTRAINT_ADDED", path, $"Reader introduces '{keyword}', whose accepted-language containment is not inferred automatically.");
            else if (!string.Equals(readerValue, writerValue, StringComparison.Ordinal))
                AddReview(findings, prefix, "TEXT_CONSTRAINT_CHANGED", path, $"'{keyword}' changed and requires semantic review.");
        }
    }

    private static void CompareObjects(JsonElement reader, JsonElement writer, string path, string prefix, List<MessageContractCompatibilityFinding> findings)
    {
        HashSet<string> readerRequired = ReadStringSet(reader, "required");
        HashSet<string> writerRequired = ReadStringSet(writer, "required");
        foreach (string required in readerRequired)
        {
            if (!writerRequired.Contains(required))
                AddBreaking(findings, prefix, "REQUIRED_PROPERTY_ADDED", path + "." + required, "Reader requires a property the writer may omit.");
        }

        Dictionary<string, JsonElement> readerProperties = ReadProperties(reader);
        Dictionary<string, JsonElement> writerProperties = ReadProperties(writer);
        AdditionalProperties readerAdditional = ReadAdditionalProperties(reader);
        AdditionalProperties writerAdditional = ReadAdditionalProperties(writer);

        foreach ((string name, JsonElement writerProperty) in writerProperties.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            if (readerProperties.TryGetValue(name, out JsonElement readerProperty))
            {
                CanReaderAcceptWriter(readerProperty, writerProperty, path + ".properties." + name, prefix, findings);
            }
            else if (readerAdditional.Kind == AdditionalPropertiesKind.False)
            {
                AddBreaking(findings, prefix, "PROPERTY_REJECTED", path + ".properties." + name, "Reader is closed and does not accept a property the writer can emit.");
            }
            else if (readerAdditional.Kind == AdditionalPropertiesKind.Schema)
            {
                CanReaderAcceptWriter(readerAdditional.Schema, writerProperty, path + ".additionalProperties", prefix, findings);
            }
        }

        if (writerAdditional.Kind != AdditionalPropertiesKind.False)
        {
            if (readerAdditional.Kind == AdditionalPropertiesKind.False)
            {
                AddBreaking(findings, prefix, "ADDITIONAL_PROPERTIES_CLOSED", path, "Writer permits additional properties but reader rejects them.");
            }
            else if (writerAdditional.Kind == AdditionalPropertiesKind.True && readerAdditional.Kind == AdditionalPropertiesKind.Schema)
            {
                AddReview(findings, prefix, "ADDITIONAL_PROPERTIES_CONSTRAINED", path, "Writer permits arbitrary additional properties while reader constrains them.");
            }
            else if (writerAdditional.Kind == AdditionalPropertiesKind.Schema && readerAdditional.Kind == AdditionalPropertiesKind.Schema)
            {
                CanReaderAcceptWriter(readerAdditional.Schema, writerAdditional.Schema, path + ".additionalProperties", prefix, findings);
            }

            foreach ((string name, JsonElement readerProperty) in readerProperties)
            {
                if (writerProperties.ContainsKey(name))
                    continue;

                if (writerAdditional.Kind == AdditionalPropertiesKind.True && !IsAcceptAll(readerProperty))
                {
                    AddReview(findings, prefix, "OPEN_WRITER_KNOWN_READER_PROPERTY", path + ".properties." + name,
                        "Writer may emit arbitrary data for a property that the reader constrains; safe containment requires review.");
                }
                else if (writerAdditional.Kind == AdditionalPropertiesKind.Schema)
                {
                    CanReaderAcceptWriter(readerProperty, writerAdditional.Schema, path + ".properties." + name, prefix, findings);
                }
            }
        }
    }

    private static void CompareArrays(JsonElement reader, JsonElement writer, string path, string prefix, List<MessageContractCompatibilityFinding> findings)
    {
        bool hasReaderItems = reader.TryGetProperty("items", out JsonElement readerItems);
        bool hasWriterItems = writer.TryGetProperty("items", out JsonElement writerItems);
        if (!hasReaderItems)
            return;
        if (!hasWriterItems)
        {
            AddReview(findings, prefix, "WRITER_ITEMS_UNBOUNDED", path + ".items", "Writer array item schema is unbounded while reader constrains items.");
            return;
        }
        CanReaderAcceptWriter(readerItems, writerItems, path + ".items", prefix, findings);
    }

    private static void CompareUnsupportedKeywords(JsonElement reader, JsonElement writer, string path, string prefix, List<MessageContractCompatibilityFinding> findings)
    {
        var names = reader.EnumerateObject().Select(static property => property.Name)
            .Concat(writer.EnumerateObject().Select(static property => property.Name))
            .Distinct(StringComparer.Ordinal)
            .Where(name => !AnnotationKeywords.Contains(name) && !SupportedKeywords.Contains(name));

        foreach (string name in names.Order(StringComparer.Ordinal))
        {
            bool hasReader = reader.TryGetProperty(name, out JsonElement readerValue);
            bool hasWriter = writer.TryGetProperty(name, out JsonElement writerValue);
            string detail = hasReader && hasWriter && JsonElement.DeepEquals(readerValue, writerValue)
                ? "is present in both schemas while other schema content changed"
                : "changed or is present on only one side";
            AddReview(findings, prefix, "UNSUPPORTED_KEYWORD_" + NormalizeCode(name), path + "." + name,
                $"JSON Schema keyword '{name}' {detail}; TCJ does not claim safe compatibility for this construct.");
        }
    }

    private static MessageContractCompatibilityResult Result(MessageContractCompatibilityMode mode, IReadOnlyList<MessageContractCompatibilityFinding> findings)
    {
        MessageContractCompatibilityStatus status = findings.Any(static finding => finding.Status == MessageContractCompatibilityStatus.Breaking)
            ? MessageContractCompatibilityStatus.Breaking
            : findings.Any(static finding => finding.Status == MessageContractCompatibilityStatus.ReviewRequired)
                ? MessageContractCompatibilityStatus.ReviewRequired
                : MessageContractCompatibilityStatus.Compatible;

        return new MessageContractCompatibilityResult
        {
            Mode = mode,
            Status = status,
            Findings = findings
                .OrderByDescending(static finding => finding.Status)
                .ThenBy(static finding => finding.Path, StringComparer.Ordinal)
                .ThenBy(static finding => finding.Code, StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static MessageContractCompatibilityMode Normalize(MessageContractCompatibilityMode mode) => mode switch
    {
        MessageContractCompatibilityMode.BackwardTransitive => MessageContractCompatibilityMode.Backward,
        MessageContractCompatibilityMode.ForwardTransitive => MessageContractCompatibilityMode.Forward,
        MessageContractCompatibilityMode.FullTransitive => MessageContractCompatibilityMode.Full,
        _ => mode
    };

    private static bool IsTransitive(MessageContractCompatibilityMode mode) => mode is
        MessageContractCompatibilityMode.BackwardTransitive or
        MessageContractCompatibilityMode.ForwardTransitive or
        MessageContractCompatibilityMode.FullTransitive;

    private static bool ValidateSupportedKeywordShapes(
        JsonElement schema,
        string path,
        string prefix,
        List<MessageContractCompatibilityFinding> findings)
    {
        bool valid = true;
        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in schema.EnumerateObject())
        {
            if (!propertyNames.Add(property.Name))
            {
                AddReview(findings, prefix, "DUPLICATE_SCHEMA_KEYWORD", path + "." + property.Name,
                    "Schema contains a duplicate JSON object member and cannot be interpreted unambiguously.");
                valid = false;
            }
        }

        if (schema.TryGetProperty("type", out JsonElement type))
        {
            string[] allowed = ["null", "boolean", "object", "array", "string", "number", "integer"];
            bool typeValid = type.ValueKind == JsonValueKind.String && allowed.Contains(type.GetString(), StringComparer.Ordinal);
            if (type.ValueKind == JsonValueKind.Array)
            {
                string[] values = type.EnumerateArray()
                    .Where(static item => item.ValueKind == JsonValueKind.String)
                    .Select(static item => item.GetString()!)
                    .ToArray();
                typeValid = values.Length > 0 && values.Length == type.GetArrayLength() &&
                            values.Distinct(StringComparer.Ordinal).Count() == values.Length &&
                            values.All(value => allowed.Contains(value, StringComparer.Ordinal));
            }
            if (!typeValid)
            {
                AddReview(findings, prefix, "MALFORMED_TYPE", path + ".type", "Schema 'type' is malformed or contains an unsupported JSON type name.");
                valid = false;
            }
        }

        if (schema.TryGetProperty("properties", out JsonElement properties) && properties.ValueKind != JsonValueKind.Object)
        {
            AddReview(findings, prefix, "MALFORMED_PROPERTIES", path + ".properties", "Schema 'properties' must be an object.");
            valid = false;
        }

        if (schema.TryGetProperty("required", out JsonElement required))
        {
            string[] requiredValues = required.ValueKind == JsonValueKind.Array
                ? required.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.String).Select(static item => item.GetString()!).ToArray()
                : [];
            if (required.ValueKind != JsonValueKind.Array || requiredValues.Length != required.GetArrayLength() ||
                requiredValues.Distinct(StringComparer.Ordinal).Count() != requiredValues.Length)
            {
                AddReview(findings, prefix, "MALFORMED_REQUIRED", path + ".required", "Schema 'required' must be an array of unique property names.");
                valid = false;
            }
        }

        foreach (string keyword in new[] { "additionalProperties", "items" })
        {
            if (schema.TryGetProperty(keyword, out JsonElement value) && value.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
            {
                AddReview(findings, prefix, "MALFORMED_" + NormalizeCode(keyword), path + "." + keyword, $"Schema '{keyword}' must be a boolean or schema object.");
                valid = false;
            }
        }

        if (schema.TryGetProperty("enum", out JsonElement enumValue) && (enumValue.ValueKind != JsonValueKind.Array || enumValue.GetArrayLength() == 0))
        {
            AddReview(findings, prefix, "MALFORMED_ENUM", path + ".enum", "Schema 'enum' must be a non-empty array.");
            valid = false;
        }

        foreach (string keyword in new[] { "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum" })
        {
            if (schema.TryGetProperty(keyword, out JsonElement value) && (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out _)))
            {
                AddReview(findings, prefix, "UNSUPPORTED_NUMERIC_PRECISION", path + "." + keyword,
                    $"Schema '{keyword}' is not a decimal value that the conservative analyzer can compare safely.");
                valid = false;
            }
        }

        foreach (string keyword in new[] { "minLength", "maxLength", "minItems", "maxItems" })
        {
            if (schema.TryGetProperty(keyword, out JsonElement value) &&
                (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long integer) || integer < 0))
            {
                AddReview(findings, prefix, "MALFORMED_" + NormalizeCode(keyword), path + "." + keyword,
                    $"Schema '{keyword}' must be a non-negative integer within supported bounds.");
                valid = false;
            }
        }

        foreach (string keyword in new[] { "pattern", "format" })
        {
            if (schema.TryGetProperty(keyword, out JsonElement value) && value.ValueKind != JsonValueKind.String)
            {
                AddReview(findings, prefix, "MALFORMED_" + NormalizeCode(keyword), path + "." + keyword, $"Schema '{keyword}' must be a string.");
                valid = false;
            }
        }

        return valid;
    }

    private static NumericBound? ReadEffectiveLowerBound(JsonElement schema)
    {
        decimal? inclusive = ReadDecimal(schema, "minimum");
        decimal? exclusive = ReadDecimal(schema, "exclusiveMinimum");
        if (inclusive is null)
            return exclusive is null ? null : new NumericBound(exclusive.Value, true);
        if (exclusive is null)
            return new NumericBound(inclusive.Value, false);
        if (exclusive > inclusive)
            return new NumericBound(exclusive.Value, true);
        if (exclusive < inclusive)
            return new NumericBound(inclusive.Value, false);
        return new NumericBound(inclusive.Value, true);
    }

    private static NumericBound? ReadEffectiveUpperBound(JsonElement schema)
    {
        decimal? inclusive = ReadDecimal(schema, "maximum");
        decimal? exclusive = ReadDecimal(schema, "exclusiveMaximum");
        if (inclusive is null)
            return exclusive is null ? null : new NumericBound(exclusive.Value, true);
        if (exclusive is null)
            return new NumericBound(inclusive.Value, false);
        if (exclusive < inclusive)
            return new NumericBound(exclusive.Value, true);
        if (exclusive > inclusive)
            return new NumericBound(inclusive.Value, false);
        return new NumericBound(inclusive.Value, true);
    }

    private static HashSet<string>? ReadTypes(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out JsonElement type))
            return null;
        if (type.ValueKind == JsonValueKind.String)
            return new HashSet<string>([type.GetString()!], StringComparer.Ordinal);
        if (type.ValueKind == JsonValueKind.Array)
            return type.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        return null;
    }

    private static bool MayContainType(JsonElement schema, string type)
    {
        HashSet<string>? types = ReadTypes(schema);
        return types is null || types.Contains(type) || (type == "number" && types.Contains("integer"));
    }

    private static Dictionary<string, JsonElement> ReadProperties(JsonElement schema)
    {
        if (!schema.TryGetProperty("properties", out JsonElement properties) || properties.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        return properties.EnumerateObject().ToDictionary(static property => property.Name, static property => property.Value, StringComparer.Ordinal);
    }

    private static HashSet<string> ReadStringSet(JsonElement schema, string name)
    {
        if (!schema.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
            return new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
    }

    private static bool EnumContains(JsonElement enumElement, JsonElement value) =>
        enumElement.ValueKind == JsonValueKind.Array && enumElement.EnumerateArray().Any(item => JsonElement.DeepEquals(item, value));

    private static decimal? ReadDecimal(JsonElement schema, string name) =>
        schema.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal result)
            ? result
            : null;

    private static long? ReadInt64(JsonElement schema, string name) =>
        schema.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result)
            ? result
            : null;

    private static string? ReadString(JsonElement schema, string name) =>
        schema.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsAcceptAll(JsonElement schema) => schema.ValueKind == JsonValueKind.True ||
        (schema.ValueKind == JsonValueKind.Object && !schema.EnumerateObject().Any());

    private static AdditionalProperties ReadAdditionalProperties(JsonElement schema)
    {
        if (!schema.TryGetProperty("additionalProperties", out JsonElement value))
            return new AdditionalProperties(AdditionalPropertiesKind.True, default);
        return value.ValueKind switch
        {
            JsonValueKind.False => new AdditionalProperties(AdditionalPropertiesKind.False, default),
            JsonValueKind.True => new AdditionalProperties(AdditionalPropertiesKind.True, default),
            JsonValueKind.Object => new AdditionalProperties(AdditionalPropertiesKind.Schema, value),
            _ => new AdditionalProperties(AdditionalPropertiesKind.True, default)
        };
    }

    private static void AddBreaking(List<MessageContractCompatibilityFinding> findings, string prefix, string code, string path, string message) =>
        findings.Add(new MessageContractCompatibilityFinding($"{prefix}:{code}", MessageContractCompatibilityStatus.Breaking, path, Bounded(message)));

    private static void AddReview(List<MessageContractCompatibilityFinding> findings, string prefix, string code, string path, string message) =>
        findings.Add(new MessageContractCompatibilityFinding($"{prefix}:{code}", MessageContractCompatibilityStatus.ReviewRequired, path, Bounded(message)));

    private static string NormalizeCode(string keyword)
    {
        string value = new(keyword.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return string.IsNullOrEmpty(value) ? "UNKNOWN" : value;
    }

    private static string Bounded(string value) => value.Length <= 512 ? value : value[..512];

    private enum AdditionalPropertiesKind { True, False, Schema }
    private readonly record struct AdditionalProperties(AdditionalPropertiesKind Kind, JsonElement Schema);
    private readonly record struct NumericBound(decimal Value, bool Exclusive);
}
