namespace TCJ.Messaging.AsyncApi;

internal static class GovernedArtifactPath
{
    public static bool TryResolve(
        string root,
        string relativePath,
        out string fullPath,
        out string normalizedRelativePath)
    {
        fullPath = string.Empty;
        normalizedRelativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Any(char.IsControl))
            return false;

        string candidateRelative = relativePath.Replace('\\', '/');
        if (candidateRelative.StartsWith("/", StringComparison.Ordinal) ||
            Path.IsPathRooted(candidateRelative) ||
            candidateRelative.Contains(':'))
            return false;

        string[] sourceSegments = candidateRelative.Split('/', StringSplitOptions.None);
        if (sourceSegments.Length == 0 || sourceSegments.Any(static segment => string.IsNullOrEmpty(segment) || segment is "."))
            return false;
        if (sourceSegments.Any(static segment => segment == ".."))
            return false;

        var normalizedSegments = new List<string>(sourceSegments);
        if (normalizedSegments.Count == 0)
            return false;

        normalizedRelativePath = string.Join('/', normalizedSegments);
        string canonicalRoot = Path.GetFullPath(root);
        string candidate = Path.GetFullPath(Path.Combine(
            canonicalRoot,
            normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = canonicalRoot.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(prefix, comparison))
            return false;

        if (TraversesExistingReparsePoint(canonicalRoot, normalizedSegments))
            return false;

        fullPath = candidate;
        return true;
    }

    private static bool TraversesExistingReparsePoint(string root, IReadOnlyList<string> segments)
    {
        if (Path.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            return true;

        string current = root;
        foreach (string segment in segments)
        {
            current = Path.Combine(current, segment);
            if (!Path.Exists(current))
                continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
        }
        return false;
    }
}
