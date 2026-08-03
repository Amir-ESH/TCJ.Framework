using System.Reflection;
using System.Runtime.Loader;

namespace TCJ.Architecture.Tests.Infrastructure;

internal static class ProductionAssemblies
{
    private static readonly Lazy<ArchitecturePolicy> Policy = new(ArchitecturePolicy.Load);

    private static readonly Lazy<IReadOnlyDictionary<string, Assembly>> Loaded = new(LoadAll);

    public static ArchitecturePolicy CurrentPolicy => Policy.Value;

    public static IReadOnlyDictionary<string, Assembly> All => Loaded.Value;

    private static IReadOnlyDictionary<string, Assembly> LoadAll()
    {
        var assemblies = new Dictionary<string, Assembly>(StringComparer.Ordinal);

        foreach (var assemblyName in CurrentPolicy.Assemblies.Keys.Order(StringComparer.Ordinal))
        {
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");
            if (!File.Exists(assemblyPath))
            {
                throw new FileNotFoundException(
                    $"Production assembly '{assemblyName}' was not copied to the architecture-test output. " +
                    "Ensure the test project directly references every production project.",
                    assemblyPath);
            }

            assemblies[assemblyName] = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(
                    assembly.GetName().Name,
                    assemblyName,
                    StringComparison.Ordinal))
                ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        }

        return assemblies;
    }
}
