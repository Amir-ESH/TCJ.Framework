using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Extensions;
using TCJ.EntityFrameworkCore.Searching;

namespace TCJ.EntityFrameworkCore.Tests;

public sealed class AotCompatibilityContractTests
{
    [Fact]
    public void Reflection_driven_model_discovery_apis_are_explicitly_restricted()
    {
        MethodInfo registerConfigurations = typeof(ModelBuilderExtensions)
            .GetMethod(nameof(ModelBuilderExtensions.RegisterEntityTypeConfiguration))!;
        MethodInfo registerEntities = typeof(ModelBuilderExtensions)
            .GetMethods()
            .Single(method => method.Name == nameof(ModelBuilderExtensions.RegisterAllEntities));
        MethodInfo getModuleAssemblies = typeof(ModelBuilderExtensions)
            .GetMethod(nameof(ModelBuilderExtensions.GetModuleAssemblies))!;

        AssertRestricted(registerConfigurations, requiresDynamicCode: true);
        AssertRestricted(registerEntities, requiresDynamicCode: true);
        AssertRestricted(getModuleAssemblies, requiresDynamicCode: false);
    }

    [Fact]
    public void Read_context_set_contract_preserves_entity_members_required_by_ef_core()
    {
        MethodInfo set = typeof(IReadDbContext).GetMethod(nameof(IReadDbContext.Set))!;
        Type entityParameter = set.GetGenericArguments().Single();
        DynamicallyAccessedMembersAttribute? annotation =
            entityParameter.GetCustomAttribute<DynamicallyAccessedMembersAttribute>();

        Assert.NotNull(annotation);
        DynamicallyAccessedMemberTypes expected =
            DynamicallyAccessedMemberTypes.PublicConstructors
            | DynamicallyAccessedMemberTypes.NonPublicConstructors
            | DynamicallyAccessedMemberTypes.PublicProperties
            | DynamicallyAccessedMemberTypes.PublicFields
            | DynamicallyAccessedMemberTypes.NonPublicProperties
            | DynamicallyAccessedMemberTypes.NonPublicFields
            | DynamicallyAccessedMemberTypes.Interfaces;
        Assert.Equal(expected, annotation.MemberTypes);
    }

    [Fact]
    public void Runtime_entity_search_apis_are_explicitly_restricted()
    {
        foreach (string methodName in new[] { nameof(IEntitySearcher.ExistsAsync), nameof(IEntitySearcher.FindAsync) })
        {
            MethodInfo contract = typeof(IEntitySearcher).GetMethod(methodName)!;
            MethodInfo implementation = typeof(EntitySearcher).GetMethod(methodName)!;

            AssertRestricted(contract, requiresDynamicCode: true);
            AssertRestricted(implementation, requiresDynamicCode: true);
        }
    }

    private static void AssertRestricted(MethodInfo method, bool requiresDynamicCode)
    {
        RequiresUnreferencedCodeAttribute? trim = method.GetCustomAttribute<RequiresUnreferencedCodeAttribute>();
        Assert.NotNull(trim);
        Assert.Contains("Native AOT", trim.Message, StringComparison.OrdinalIgnoreCase);

        RequiresDynamicCodeAttribute? dynamicCode = method.GetCustomAttribute<RequiresDynamicCodeAttribute>();
        if (requiresDynamicCode)
        {
            Assert.NotNull(dynamicCode);
            Assert.Contains("Native AOT", dynamicCode.Message, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Null(dynamicCode);
        }
    }
}
