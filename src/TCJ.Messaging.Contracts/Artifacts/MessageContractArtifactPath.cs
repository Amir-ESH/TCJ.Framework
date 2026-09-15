using System.Text;

namespace TCJ.Messaging.Contracts;

internal static class MessageContractArtifactPath
{
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string GetMessageTypeSegment(string messageType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        if (IsPortableSegment(messageType))
            return messageType;

        string encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes(messageType))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return "~" + encoded;
    }

    public static string GetContractDirectory(string messageType, int messageVersion) =>
        $"{GetMessageTypeSegment(messageType)}/v{messageVersion}";

    public static string GetSchemaPath(string messageType, int messageVersion) =>
        GetContractDirectory(messageType, messageVersion) + "/schema.json";

    private static bool IsPortableSegment(string value)
    {
        if (value.EndsWith(".", StringComparison.Ordinal))
            return false;

        int dot = value.IndexOf('.');
        string deviceBaseName = dot >= 0 ? value[..dot] : value;
        return !WindowsReservedNames.Contains(deviceBaseName);
    }
}
