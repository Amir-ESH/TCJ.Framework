using System.Reflection;
using System.Runtime.CompilerServices;
using TCJ.Architecture.Tests.Infrastructure;

namespace TCJ.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class NamingAndVisibilityArchitectureTests
{
    private static readonly ArchitecturePolicy Policy = ProductionAssemblies.CurrentPolicy;

    [Fact]
    public void Extension_method_containers_are_static_and_end_with_Extensions()
    {
        var violations = new List<string>();

        foreach (var (assemblyName, assembly) in ProductionAssemblies.All.OrderBy(pair => pair.Key))
        {
            foreach (var type in assembly.GetExportedTypes().OrderBy(type => type.FullName))
            {
                var containsExtensionMethods = type.GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Any(method => method.IsDefined(typeof(ExtensionAttribute), inherit: false));

                if (!containsExtensionMethods)
                {
                    continue;
                }

                if (!type.Name.EndsWith("Extensions", StringComparison.Ordinal))
                {
                    violations.Add(
                        $"Extension container '{type.FullName}' in assembly '{assemblyName}' must end with 'Extensions'.");
                }

                if (!(type.IsAbstract && type.IsSealed))
                {
                    violations.Add(
                        $"Extension container '{type.FullName}' in assembly '{assemblyName}' must be static.");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            ArchitectureFailure.Format("extension container naming and visibility", violations));
    }

    [Fact]
    public void Public_option_types_are_explicitly_approved()
    {
        var approved = Policy.ApprovedPublicOptionTypes.ToHashSet(StringComparer.Ordinal);
        var actual = ProductionAssemblies.All.Values
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => type.Name.EndsWith("Options", StringComparison.Ordinal))
            .Select(type => type.FullName ?? type.Name)
            .ToHashSet(StringComparer.Ordinal);

        var violations = actual.Except(approved, StringComparer.Ordinal)
            .Select(type => $"Public option type '{type}' is not listed in approvedPublicOptionTypes.")
            .Concat(approved.Except(actual, StringComparer.Ordinal)
                .Select(type => $"Approved public option type '{type}' was not found as a public production type."))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            ArchitectureFailure.Format("public option types require explicit approval", violations));
    }

    [Fact]
    public void Repository_interfaces_use_the_I_prefix()
    {
        var violations = ProductionAssemblies.All.Values
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => type.IsInterface)
            .Where(type => type.Namespace?.Contains(".Repositories", StringComparison.Ordinal) == true)
            .Where(type => type.Name.Contains("Repository", StringComparison.Ordinal))
            .Where(type => !type.Name.StartsWith('I'))
            .Select(type => $"Repository interface '{type.FullName}' must use the 'I' prefix.")
            .ToArray();

        Assert.True(
            violations.Length == 0,
            ArchitectureFailure.Format("repository interface naming", violations));
    }

    [Fact]
    public void SQL_Server_and_ASPNET_specific_types_stay_in_their_owning_packages()
    {
        var violations = new List<string>();

        foreach (var (assemblyName, assembly) in ProductionAssemblies.All.OrderBy(pair => pair.Key))
        {
            foreach (var type in assembly.GetTypes().Where(type => !type.Name.StartsWith('<')))
            {
                var fullName = type.FullName ?? type.Name;

                if (fullName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase)
                    && !assemblyName.Equals("TCJ.EntityFrameworkCore.SqlServer", StringComparison.Ordinal))
                {
                    violations.Add(
                        $"SQL Server-specific type '{fullName}' is declared in assembly '{assemblyName}'.");
                }

                var looksAspNetSpecific = type.Name.Contains("Middleware", StringComparison.Ordinal)
                    || type.Name.EndsWith("ExceptionHandler", StringComparison.Ordinal)
                    || fullName.Contains("AspNetCore", StringComparison.Ordinal);
                if (looksAspNetSpecific
                    && !assemblyName.Equals("TCJ.AspNetCore", StringComparison.Ordinal))
                {
                    violations.Add(
                        $"ASP.NET Core-specific type '{fullName}' is declared in assembly '{assemblyName}'.");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            ArchitectureFailure.Format("infrastructure-specific type location", violations));
    }
}
