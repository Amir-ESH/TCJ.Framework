using TCJ.Architecture.Tests.Infrastructure;

namespace TCJ.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class PublicApiArchitectureTests
{
    private static readonly ArchitecturePolicy Policy = ProductionAssemblies.CurrentPolicy;

    [Fact]
    public void Lower_level_public_APIs_do_not_expose_forbidden_infrastructure_types()
    {
        var violations = new List<string>();

        foreach (var (assemblyName, assembly) in ProductionAssemblies.All.OrderBy(pair => pair.Key))
        {
            var forbiddenPrefixes = Policy.ForbiddenPublicApiTypePrefixes[assemblyName];

            foreach (var publicType in assembly.GetExportedTypes().OrderBy(type => type.FullName))
            {
                foreach (var referencedType in PublicApiInspector.GetReferencedTypes(publicType))
                {
                    var referencedName = referencedType.FullName ?? referencedType.Name;
                    var prefix = forbiddenPrefixes.FirstOrDefault(
                        candidate => referencedName.StartsWith(candidate, StringComparison.Ordinal));
                    if (prefix is null)
                    {
                        continue;
                    }

                    violations.Add(
                        $"Public API type '{publicType.FullName}' in assembly '{assemblyName}' exposes " +
                        $"'{referencedName}', which matches forbidden infrastructure prefix '{prefix}'.");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            ArchitectureFailure.Format("public API infrastructure boundaries", violations));
    }


    [Fact]
    public void Public_API_inspector_handles_constructor_and_generic_method_signatures()
    {
        var referencedTypes = PublicApiInspector.GetReferencedTypes(typeof(PublicApiInspectorFixture));

        Assert.Contains(typeof(Uri), referencedTypes);
        Assert.Contains(typeof(Stream), referencedTypes);
    }

    [Fact]
    public void Public_interfaces_do_not_expose_TCJ_implementation_classes()
    {
        var productionAssemblies = ProductionAssemblies.All.Keys.ToHashSet(StringComparer.Ordinal);
        var violations = new List<string>();

        foreach (var (assemblyName, assembly) in ProductionAssemblies.All.OrderBy(pair => pair.Key))
        {
            foreach (var interfaceType in assembly.GetExportedTypes()
                         .Where(type => type.IsInterface)
                         .OrderBy(type => type.FullName))
            {
                foreach (var referencedType in PublicApiInspector.GetReferencedTypes(interfaceType))
                {
                    var referencedAssembly = referencedType.Assembly.GetName().Name;
                    var looksImplementationOnly = referencedType.Namespace?.Contains(
                            ".Internal",
                            StringComparison.Ordinal) == true
                        || referencedType.Name.StartsWith("Ef", StringComparison.Ordinal)
                        || referencedType.Name.EndsWith("Implementation", StringComparison.Ordinal)
                        || referencedType.Name.EndsWith("HandlerInvoker", StringComparison.Ordinal);

                    if (referencedAssembly is null
                        || !productionAssemblies.Contains(referencedAssembly)
                        || referencedType.IsInterface
                        || referencedType.IsEnum
                        || referencedType.IsValueType
                        || typeof(Delegate).IsAssignableFrom(referencedType)
                        || !looksImplementationOnly)
                    {
                        continue;
                    }

                    violations.Add(
                        $"Public interface '{interfaceType.FullName}' in assembly '{assemblyName}' exposes concrete " +
                        $"implementation-only TCJ type '{referencedType.FullName}' from '{referencedAssembly}'.");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            ArchitectureFailure.Format("abstractions must not depend on TCJ concrete implementations", violations));
    }

    private sealed class PublicApiInspectorFixture
    {
        public PublicApiInspectorFixture(Uri value)
        {
            Value = value;
        }

        public Uri Value { get; }

        public T Echo<T>(T value)
            where T : Stream
            => value;
    }
}
