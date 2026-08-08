using System.Reflection;
using FsCheck.Xunit;
using Microsoft.Extensions.DependencyInjection;
using TCJ.DependencyInjection.Extensions;
using TCJ.DependencyInjection.Lifetimes;
using TCJ.DependencyInjection.Registration;
using TCJ.PropertyTests.Infrastructure;

namespace TCJ.PropertyTests;

public sealed class DependencyInjectionProperties
{
    [Property(MaxTest = 100, Replay = "1501,2501", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "DependencyInjection")]
    public bool DuplicateAssemblyScanningDoesNotDuplicateService(bool addTwice)
    {
        var options = new TcjDependencyInjectionOptions().AddAssembly(typeof(DependencyInjectionProperties).Assembly);
        if (addTwice) options.AddAssembly(typeof(DependencyInjectionProperties).Assembly);
        var services = new ServiceCollection();
        services.AddTcjDependencyInjection(options);
        return services.Count(d => d.ServiceType == typeof(IPropertyTransient)) == 1;
    }

    [Property(MaxTest = 100, Replay = "1502,2502", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "DependencyInjection")]
    public bool MarkerLifetimesRemainStable(bool registerFrameworkServices)
    {
        var services = new ServiceCollection();
        services.AddTcjDependencyInjection(new TcjDependencyInjectionOptions
        {
            RegisterFrameworkServices = registerFrameworkServices
        }.AddAssembly(typeof(DependencyInjectionProperties).Assembly));
        return FindLifetime<IPropertyTransient>(services) == ServiceLifetime.Transient
            && FindLifetime<IPropertyScoped>(services) == ServiceLifetime.Scoped
            && FindLifetime<IPropertySingleton>(services) == ServiceLifetime.Singleton;
    }

    [Property(MaxTest = 100, Replay = "1503,2503", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "DependencyInjection")]
    public bool SelfRegistrationRemainsSelfScoped(bool value)
    {
        _ = value;
        var services = new ServiceCollection();
        services.AddTcjDependencyInjection(typeof(DependencyInjectionProperties).Assembly);
        ServiceDescriptor? descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(PropertySelfScoped));
        return descriptor is not null && descriptor.Lifetime == ServiceLifetime.Scoped
            && descriptor.ImplementationType == typeof(PropertySelfScoped);
    }

    [Property(MaxTest = 100, Replay = "1504,2504", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "DependencyInjection")]
    public bool AbstractMarkedTypesAreNotRegistered(bool reverse)
    {
        Assembly[] assemblies = reverse
            ? [typeof(DependencyInjectionProperties).Assembly, typeof(TcjDependencyInjectionOptions).Assembly]
            : [typeof(TcjDependencyInjectionOptions).Assembly, typeof(DependencyInjectionProperties).Assembly];
        var services = new ServiceCollection();
        services.AddTcjDependencyInjection(assemblies);
        return services.All(d => d.ImplementationType != typeof(AbstractPropertyTransient));
    }

    [Property(MaxTest = 100, Replay = "1505,2505", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "DependencyInjection")]
    public bool OpenGenericRegistrationPreservesGenericContract(bool value)
    {
        _ = value;
        var services = new ServiceCollection();
        services.AddTcjDependencyInjection(typeof(DependencyInjectionProperties).Assembly);
        ServiceDescriptor? descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IPropertyGeneric<>));
        return descriptor is not null && descriptor.ImplementationType == typeof(PropertyGeneric<>);
    }

    private static ServiceLifetime? FindLifetime<T>(IServiceCollection services) =>
        services.SingleOrDefault(d => d.ServiceType == typeof(T))?.Lifetime;
}

public interface IPropertyTransient { }
public sealed class PropertyTransient : IPropertyTransient, ITransientDependency { }
public interface IPropertyScoped { }
public sealed class PropertyScoped : IPropertyScoped, IScopedDependency { }
public interface IPropertySingleton { }
public sealed class PropertySingleton : IPropertySingleton, ISingletonDependency { }
public sealed class PropertySelfScoped : ISelfScopedDependency { }
public abstract class AbstractPropertyTransient : IPropertyTransient, ITransientDependency { }
public interface IPropertyGeneric<T> { }
public sealed class PropertyGeneric<T> : IPropertyGeneric<T>, ITransientDependency { }
