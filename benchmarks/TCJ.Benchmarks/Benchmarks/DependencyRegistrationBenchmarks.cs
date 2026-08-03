using System.Reflection;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using TCJ.DependencyInjection.Extensions;
using TCJ.DependencyInjection.Lifetimes;

namespace TCJ.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("TCJ.DependencyInjection", "Registration")]
public class DependencyRegistrationBenchmarks
{
    private Assembly _assembly = null!;

    [GlobalSetup]
    public void Setup()
        => _assembly = typeof(DependencyRegistrationBenchmarks).Assembly;

    [Benchmark(Baseline = true)]
    public ServiceDescriptor RegisterTransientDependency()
        => RegisterAndFind(typeof(ITransientBenchmarkService));

    [Benchmark]
    public ServiceDescriptor RegisterScopedDependency()
        => RegisterAndFind(typeof(IScopedBenchmarkService));

    [Benchmark]
    public ServiceDescriptor RegisterSingletonDependency()
        => RegisterAndFind(typeof(ISingletonBenchmarkService));

    [Benchmark]
    public int RepeatedRegistrationWithDuplicateProtection()
    {
        var services = new ServiceCollection();
        services.AddTcjDependencyInjection(_assembly);
        int firstRegistrationCount = services.Count;

        services.AddTcjDependencyInjection(_assembly);

        return services.Count - firstRegistrationCount;
    }

    private ServiceDescriptor RegisterAndFind(Type serviceType)
    {
        var services = new ServiceCollection();
        services.AddTcjDependencyInjection(_assembly);

        return services.Single(descriptor => descriptor.ServiceType == serviceType);
    }
}

public interface ITransientBenchmarkService
{
}

public class TransientBenchmarkService :
    ITransientBenchmarkService,
    ITransientDependency
{
}

public interface IScopedBenchmarkService
{
}

public class ScopedBenchmarkService :
    IScopedBenchmarkService,
    IScopedDependency
{
}

public interface ISingletonBenchmarkService
{
}

public class SingletonBenchmarkService :
    ISingletonBenchmarkService,
    ISingletonDependency
{
}
