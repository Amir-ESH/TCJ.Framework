using System.Text;
using System.Text.RegularExpressions;

namespace TCJ.Messaging.Configuration;

internal static partial class MessagingValidation
{
    public const string JsonContentType = "application/json";

    public static string ValidateIdentifier(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || value.Any(char.IsControl))
            throw new ArgumentException($"{parameterName} must be {maximumLength} characters or fewer and cannot contain control characters.", parameterName);
        return value;
    }

    public static string? ValidateOptionalIdentifier(string? value, string parameterName, int maximumLength) =>
        value is null ? null : ValidateIdentifier(value, parameterName, maximumLength);

    public static string ValidateMessageType(string value, string parameterName, int maximumLength)
    {
        ValidateIdentifier(value, parameterName, maximumLength);
        // Logical message types are public compatibility contracts; CLR/assembly-qualified names are prohibited.
        if (value.Contains(',', StringComparison.Ordinal) || value.Contains("[[", StringComparison.Ordinal) || value.Contains('`'))
            throw new ArgumentException("Logical message types must not be CLR assembly-qualified or generic type names.", parameterName);
        if (!LogicalMessageTypeRegex().IsMatch(value))
            throw new ArgumentException("Logical message types may contain ASCII letters, digits, '.', '-', and '_' only.", parameterName);
        return value;
    }

    public static int ValidateVersion(int version, string parameterName)
    {
        if (version <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "Message version must be greater than zero.");
        return version;
    }

    public static string ValidateContentTypeSyntax(string contentType, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType, parameterName);
        if (contentType.Length > 128 || contentType.Any(char.IsControl))
            throw new ArgumentException("Content type must be 128 characters or fewer and cannot contain control characters.", parameterName);
        string mediaType = contentType.Split(';', 2, StringSplitOptions.TrimEntries)[0];
        int slash = mediaType.IndexOf('/');
        if (slash <= 0 || slash == mediaType.Length - 1 || mediaType.Count(static c => c == '/') != 1)
            throw new ArgumentException("Content type must contain a syntactically valid type/subtype media type.", parameterName);
        return contentType;
    }

    public static string ValidateJsonContentType(string contentType, string parameterName)
    {
        ValidateContentTypeSyntax(contentType, parameterName);
        string mediaType = contentType.Split(';', 2, StringSplitOptions.TrimEntries)[0];
        if (!string.Equals(mediaType, JsonContentType, StringComparison.OrdinalIgnoreCase) &&
            !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Content type '{contentType}' is not supported by the default TCJ JSON serializer.");
        return contentType;
    }

    public static void ValidateHeaderName(string name, string parameterName, int maximumLength)
    {
        ValidateIdentifier(name, parameterName, maximumLength);
        if (!HeaderNameRegex().IsMatch(name))
            throw new ArgumentException("Messaging header names may contain ASCII letters, digits, '-', '_', and '.' only.", parameterName);
    }

    public static void ValidateHeaderValue(string value, string parameterName, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length > maximumLength || value.Any(static c => char.IsControl(c) && c is not '\t'))
            throw new ArgumentException($"Messaging header values must be {maximumLength} characters or fewer and cannot contain control characters.", parameterName);
    }

    public static void ValidateTopologyName(string value, string parameterName, int maximumLength)
    {
        ValidateIdentifier(value, parameterName, maximumLength);
        if (!TopologyNameRegex().IsMatch(value))
            throw new ArgumentException("Topology names may contain ASCII letters, digits, '.', '-', '_', and ':' only.", parameterName);
    }

    public static int GetHeaderByteCount(IEnumerable<KeyValuePair<string, string>> headers)
    {
        int count = 0;
        foreach ((string key, string value) in headers)
            count = checked(count + Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value));
        return count;
    }

    public static bool IsValidW3CTraceParent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 55 || !TraceParentRegex().IsMatch(value))
            return false;
        string traceId = value.Substring(3, 32);
        string parentId = value.Substring(36, 16);
        return traceId.Any(static c => c != '0') && parentId.Any(static c => c != '0');
    }

    public static bool IsValidTraceState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            return false;
        return value.Split(',').All(static member => member.Contains('=', StringComparison.Ordinal) && member.Length <= 256);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex LogicalMessageTypeRegex();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderNameRegex();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex TopologyNameRegex();
    [GeneratedRegex("^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex TraceParentRegex();
}
