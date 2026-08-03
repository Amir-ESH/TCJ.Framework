using System.Reflection;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using TCJ.DependencyInjection.Lifetimes;

namespace TCJ.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("TCJ.DependencyInjection", "Discovery")]
public class DependencyDiscoveryBenchmarks
{
    private Assembly _assembly = null!;
    private Type[] _publicConcreteTypes = null!;

    [GlobalSetup]
    public void Setup()
    {
        _assembly = typeof(DependencyDiscoveryBenchmarks).Assembly;
        _publicConcreteTypes = DiscoverTypes(_assembly);
    }

    [Benchmark(Baseline = true)]
    public Type[] DiscoverPublicConcreteTypes()
        => DiscoverTypes(_assembly);

    [Benchmark]
    public int ClassifyDependencyMarkers()
        => _publicConcreteTypes.Count(
            static type => typeof(IDependency).IsAssignableFrom(type));

    private static Type[] DiscoverTypes(Assembly assembly)
        => assembly
            .GetTypes()
            .Where(static type => type.IsClass && !type.IsAbstract)
            .Where(static type => type.IsPublic || type.IsNestedPublic)
            .Where(static type => !type.IsDefined(
                typeof(CompilerGeneratedAttribute),
                inherit: false))
            .ToArray();
}
