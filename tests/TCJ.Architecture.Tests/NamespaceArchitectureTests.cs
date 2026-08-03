using System.Runtime.CompilerServices;
using TCJ.Architecture.Tests.Infrastructure;

namespace TCJ.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class NamespaceArchitectureTests
{
    private static readonly ArchitecturePolicy Policy = ProductionAssemblies.CurrentPolicy;

    [Fact]
    public void Production_types_use_their_owning_package_namespace()
    {
        var violations = new List<string>();

        foreach (var (assemblyName, assembly) in ProductionAssemblies.All.OrderBy(pair => pair.Key))
        {
            var expectedRoot = Policy.NamespaceRoots[assemblyName];

            foreach (var type in assembly.GetTypes().Where(IsSourceDeclaredType))
            {
                var typeNamespace = type.Namespace ?? "<global namespace>";
                if (type.Namespace is null
                    || !(type.Namespace.Equals(expectedRoot, StringComparison.Ordinal)
                        || type.Namespace.StartsWith(expectedRoot + ".", StringComparison.Ordinal)))
                {
                    violations.Add(
                        $"Type '{type.FullName ?? type.Name}' belongs to assembly '{assemblyName}' but is declared " +
                        $"under namespace '{typeNamespace}'. Expected namespace root '{expectedRoot}'.");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            ArchitectureFailure.Format("package namespace ownership", violations));
    }

    [Fact]
    public void Internal_namespace_types_are_not_public_API()
    {
        var violations = ProductionAssemblies.All
            .SelectMany(pair => pair.Value.GetExportedTypes()
                .Where(type => HasNamespaceSegment(type.Namespace, "Internal"))
                .Select(type =>
                    $"Public type '{type.FullName}' is inside an Internal namespace in assembly '{pair.Key}'."))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            ArchitectureFailure.Format("Internal namespaces must not expose public types", violations));
    }

    [Fact]
    public void Production_assemblies_do_not_contain_test_only_namespaces_or_types()
    {
        var violations = new List<string>();

        foreach (var (assemblyName, assembly) in ProductionAssemblies.All.OrderBy(pair => pair.Key))
        {
            foreach (var type in assembly.GetTypes().Where(IsSourceDeclaredType))
            {
                if (HasNamespaceSegment(type.Namespace, "Tests")
                    || type.Name.EndsWith("Tests", StringComparison.Ordinal)
                    || type.Name.EndsWith("TestFixture", StringComparison.Ordinal))
                {
                    violations.Add(
                        $"Production assembly '{assemblyName}' contains test-only type '{type.FullName ?? type.Name}'.");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            ArchitectureFailure.Format("test-only types must not enter production assemblies", violations));
    }

    private static bool IsSourceDeclaredType(Type type)
        => !type.Name.StartsWith('<')
            && !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false);

    private static bool HasNamespaceSegment(string? value, string segment)
        => value?.Split('.').Contains(segment, StringComparer.Ordinal) == true;
}
