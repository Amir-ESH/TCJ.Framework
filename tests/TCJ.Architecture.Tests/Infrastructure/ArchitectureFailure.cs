namespace TCJ.Architecture.Tests.Infrastructure;

internal static class ArchitectureFailure
{
    public static string Format(string rule, IEnumerable<string> violations)
    {
        var ordered = violations
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return $"Architecture rule failed: {rule}{Environment.NewLine}" +
               string.Join(Environment.NewLine, ordered.Select(value => $"  - {value}")) +
               Environment.NewLine +
               $"Policy: {ArchitecturePolicy.RelativePath}{Environment.NewLine}" +
               $"Documentation: {ArchitecturePolicy.DocumentationPath}{Environment.NewLine}" +
               "Intentional changes must update the policy, architecture documentation, and pull-request justification.";
    }
}
