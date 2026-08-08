using Microsoft.Extensions.DependencyInjection;
using TCJ.DependencyInjection.Extensions;
using TCJ.DependencyInjection.Lifetimes;
using TCJ.DependencyInjection.Registration;

namespace TCJ.FuzzTests.Targets;

internal sealed class DependencyScanningTarget : IFuzzTarget
{
    public string Name => "DependencyScanning";

    public void Execute(ReadOnlyMemory<byte> input)
    {
        bool duplicate = input.Length > 0 && (input.Span[0] & 1) != 0;
        bool framework = input.Length < 2 || (input.Span[1] & 1) != 0;
        var options = new TcjDependencyInjectionOptions { RegisterFrameworkServices = framework }
            .AddAssembly(typeof(DependencyScanningTarget).Assembly);
        if (duplicate) options.AddAssembly(typeof(DependencyScanningTarget).Assembly);

        var services = new ServiceCollection();
        services.AddTcjDependencyInjection(options);
        ServiceDescriptor[] descriptors = services.Where(d => d.ServiceType == typeof(IFuzzTransient)).ToArray();
        ServiceDescriptor? self = services.SingleOrDefault(d => d.ServiceType == typeof(FuzzSelfScoped));
        ServiceDescriptor? generic = services.SingleOrDefault(d => d.ServiceType == typeof(IFuzzGeneric<>));
        if (descriptors.Length != 1 || descriptors[0].Lifetime != ServiceLifetime.Transient
            || self?.Lifetime != ServiceLifetime.Scoped
            || generic?.ImplementationType != typeof(FuzzGeneric<>)
            || services.Any(d => d.ImplementationType == typeof(FuzzAbstractTransient)))
            throw new FuzzInvariantException("Dependency scanning produced an unstable or unsupported registration.");
    }
}

public interface IFuzzTransient { }
public sealed class FuzzTransient : IFuzzTransient, ITransientDependency { }
public sealed class FuzzSelfScoped : ISelfScopedDependency { }
public abstract class FuzzAbstractTransient : IFuzzTransient, ITransientDependency { }
public interface IFuzzGeneric<T> { }
public sealed class FuzzGeneric<T> : IFuzzGeneric<T>, ITransientDependency { }
