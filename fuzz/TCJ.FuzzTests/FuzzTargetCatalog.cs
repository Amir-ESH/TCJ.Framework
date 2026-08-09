using TCJ.FuzzTests.Targets;

namespace TCJ.FuzzTests;

internal static class FuzzTargetCatalog
{
    private static readonly IReadOnlyDictionary<string, Func<IFuzzTarget>> Factories =
        new Dictionary<string, Func<IFuzzTarget>>(StringComparer.Ordinal)
        {
            ["StringExtensions"] = static () => new StringExtensionsTarget(),
            ["Check"] = static () => new CheckTarget(),
            ["EnumerableExtensions"] = static () => new EnumerableExtensionsTarget(),
            ["DependencyScanning"] = static () => new DependencyScanningTarget(),
            ["ResultComposition"] = static () => new ResultCompositionTarget()
        };

    private static readonly IReadOnlyCollection<string> TargetNames = Factories.Keys.ToArray();

    public static IReadOnlyCollection<string> Names => TargetNames;

    public static IFuzzTarget Create(string name) =>
        Factories.TryGetValue(name, out Func<IFuzzTarget>? factory)
            ? factory()
            : throw new ArgumentException($"Unknown fuzz target '{name}'.", nameof(name));
}
