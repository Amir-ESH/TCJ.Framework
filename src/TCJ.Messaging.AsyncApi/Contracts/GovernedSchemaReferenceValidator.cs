using System.Globalization;
using System.Text.Json;

namespace TCJ.Messaging.AsyncApi;

internal sealed class GovernedSchemaReferenceValidator
{
    private readonly string _artifactRoot;
    private readonly GovernedContractResolutionOptions _options;
    private readonly List<GovernedContractValidationError> _errors;
    private readonly HashSet<string> _visited = new(StringComparer.Ordinal);

    public GovernedSchemaReferenceValidator(
        string artifactRoot,
        GovernedContractResolutionOptions options,
        List<GovernedContractValidationError> errors)
    {
        _artifactRoot = artifactRoot;
        _options = options;
        _errors = errors;
    }

    public void Validate(string schemaRelativePath, ReadOnlyMemory<byte> schemaUtf8)
    {
        _visited.Clear();
        ValidateDocument(schemaRelativePath, schemaUtf8, depth: 0);
    }

    private void ValidateDocument(string relativePath, ReadOnlyMemory<byte> utf8, int depth)
    {
        if (depth > _options.MaximumReferenceDepth)
        {
            Add(GovernedContractValidationCodes.ReferenceDepthExceeded, relativePath, "Local schema reference depth exceeds the configured bound.");
            return;
        }

        if (!_visited.Add(relativePath))
            return;

        if (!TryParse(utf8, relativePath, out JsonDocument document))
            return;

        using JsonDocument ownedDocument = document;
        foreach (string? reference in EnumerateReferences(ownedDocument.RootElement))
        {
            if (reference is null)
            {
                Add(GovernedContractValidationCodes.InvalidLocalReference, relativePath, "Schema $ref values must be strings.");
                continue;
            }
            ValidateReference(relativePath, ownedDocument.RootElement, reference, depth);
        }
    }

    private void ValidateReference(string sourceRelativePath, JsonElement sourceRoot, string reference, int depth)
    {
        if (Uri.TryCreate(reference, UriKind.Absolute, out _))
        {
            Add(GovernedContractValidationCodes.RemoteReferenceNotAllowed, sourceRelativePath, "Remote or absolute schema references are not allowed and are never fetched.");
            return;
        }

        int hashIndex = reference.IndexOf('#');
        string pathPart = hashIndex >= 0 ? reference[..hashIndex] : reference;
        string fragmentPart = hashIndex >= 0 ? reference[(hashIndex + 1)..] : string.Empty;

        string decodedPath;
        string decodedFragment;
        try
        {
            decodedPath = Uri.UnescapeDataString(pathPart);
            decodedFragment = Uri.UnescapeDataString(fragmentPart);
        }
        catch (UriFormatException)
        {
            Add(GovernedContractValidationCodes.InvalidLocalReference, sourceRelativePath, "Schema $ref contains invalid URI escaping.");
            return;
        }

        if (decodedPath.Contains('?') || decodedPath.Contains('\\') || decodedPath.Contains(':'))
        {
            Add(GovernedContractValidationCodes.InvalidLocalReference, sourceRelativePath, "Schema $ref is not a supported local artifact reference.");
            return;
        }

        string targetRelativePath = sourceRelativePath;
        ReadOnlyMemory<byte> targetUtf8 = default;
        JsonElement targetRoot = sourceRoot;
        JsonDocument? targetDocument = null;

        if (!string.IsNullOrEmpty(decodedPath))
        {
            if (decodedPath.StartsWith("/", StringComparison.Ordinal) ||
                decodedPath.Split('/', StringSplitOptions.None).Any(static segment => segment is "." or ".." or ""))
            {
                Add(GovernedContractValidationCodes.SchemaPathTraversal, sourceRelativePath, "Local schema reference contains a prohibited traversal or rooted path.");
                return;
            }

            string sourceDirectory = GetDirectory(sourceRelativePath);
            string combined = string.IsNullOrEmpty(sourceDirectory) ? decodedPath : sourceDirectory + "/" + decodedPath;
            if (!GovernedArtifactPath.TryResolve(_artifactRoot, combined, out string targetPath, out targetRelativePath))
            {
                Add(GovernedContractValidationCodes.SchemaPathTraversal, sourceRelativePath, "Local schema reference escapes or traverses the configured artifact root.");
                return;
            }
            if (!File.Exists(targetPath))
            {
                Add(GovernedContractValidationCodes.InvalidLocalReference, targetRelativePath, "Referenced local schema artifact does not exist.");
                return;
            }
            if (!TryReadBounded(targetPath, _options.MaximumSchemaBytes, targetRelativePath, out targetUtf8))
                return;

            try
            {
                targetDocument = JsonDocument.Parse(targetUtf8, JsonOptions());
                targetRoot = targetDocument.RootElement;
            }
            catch (JsonException)
            {
                Add(GovernedContractValidationCodes.InvalidLocalReference, targetRelativePath, "Referenced local schema artifact is not valid JSON.");
                targetDocument?.Dispose();
                return;
            }
        }

        try
        {
            if (!FragmentExists(targetRoot, decodedFragment))
            {
                Add(GovernedContractValidationCodes.InvalidLocalReference, targetRelativePath, "Schema $ref fragment does not resolve to a local target.");
                return;
            }

            if (!string.IsNullOrEmpty(decodedPath) && !_visited.Contains(targetRelativePath))
            {
                if (depth >= _options.MaximumReferenceDepth)
                {
                    Add(GovernedContractValidationCodes.ReferenceDepthExceeded, targetRelativePath, "Local schema reference depth exceeds the configured bound.");
                    return;
                }
                ValidateDocument(targetRelativePath, targetUtf8, depth + 1);
            }
        }
        finally
        {
            targetDocument?.Dispose();
        }
    }

    private bool TryParse(ReadOnlyMemory<byte> utf8, string relativePath, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(utf8, JsonOptions());
            return true;
        }
        catch (JsonException)
        {
            Add(GovernedContractValidationCodes.SchemaMalformed, relativePath, "Governed schema is not structurally valid JSON.");
            document = null!;
            return false;
        }
    }

    private bool TryReadBounded(string path, int maximumBytes, string relativePath, out ReadOnlyMemory<byte> bytes)
    {
        bytes = default;
        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > maximumBytes || info.Length > int.MaxValue)
            {
                Add(GovernedContractValidationCodes.InvalidLocalReference, relativePath, "Referenced local schema artifact exceeds the configured size bound.");
                return false;
            }
            bytes = File.ReadAllBytes(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Add(GovernedContractValidationCodes.ArtifactReadFailure, relativePath, "Referenced local schema artifact could not be read.");
            return false;
        }
    }

    private static IEnumerable<string?> EnumerateReferences(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "$ref", StringComparison.Ordinal))
                    {
                        yield return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                    }
                    else
                    {
                        foreach (string? nested in EnumerateReferences(property.Value))
                            yield return nested;
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    foreach (string? nested in EnumerateReferences(item))
                        yield return nested;
                }
                break;
        }
    }

    private static bool FragmentExists(JsonElement root, string fragment)
    {
        if (string.IsNullOrEmpty(fragment))
            return true;
        if (!fragment.StartsWith("/", StringComparison.Ordinal))
            return ContainsAnchor(root, fragment);

        JsonElement current = root;
        foreach (string rawToken in fragment.Split('/').Skip(1))
        {
            if (!TryDecodePointerToken(rawToken, out string token))
                return false;
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(token, out JsonElement child))
                    return false;
                current = child;
                continue;
            }
            if (current.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index < 0 || index >= current.GetArrayLength())
                    return false;
                current = current[index];
                continue;
            }
            return false;
        }
        return true;
    }

    private static bool TryDecodePointerToken(string value, out string decoded)
    {
        decoded = string.Empty;
        var buffer = new System.Text.StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current != '~')
            {
                buffer.Append(current);
                continue;
            }
            if (++index >= value.Length)
                return false;
            char escaped = value[index];
            if (escaped == '0') buffer.Append('~');
            else if (escaped == '1') buffer.Append('/');
            else return false;
        }
        decoded = buffer.ToString();
        return true;
    }

    private static bool ContainsAnchor(JsonElement element, string anchor)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if ((property.NameEquals("$anchor") || property.NameEquals("$dynamicAnchor")) &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    string.Equals(property.Value.GetString(), anchor, StringComparison.Ordinal))
                    return true;
                if (ContainsAnchor(property.Value, anchor))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (ContainsAnchor(item, anchor))
                    return true;
            }
        }
        return false;
    }

    private static JsonDocumentOptions JsonOptions() => new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 128
    };

    private static string GetDirectory(string relativePath)
    {
        int separator = relativePath.LastIndexOf('/');
        return separator < 0 ? string.Empty : relativePath[..separator];
    }

    private void Add(string code, string path, string message) => _errors.Add(new(code, path, message));
}
