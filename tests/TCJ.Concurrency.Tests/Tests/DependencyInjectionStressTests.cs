using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Concurrency.Tests.Fixtures;
using TCJ.Concurrency.Tests.Infrastructure;
using TCJ.Core.DomainEvents;
using TCJ.Core.Identifiers;
using TCJ.DependencyInjection.Extensions;

namespace TCJ.Concurrency.Tests.Tests;

[Trait("Category", "Concurrency")]
[Trait("Category", "Stress")]
[Trait("Category", "DependencyInjection")]
public sealed class DependencyInjectionStressTests
{
    private static readonly Assembly TestAssembly = typeof(TransientProbe).Assembly;

    [Fact]
    public Task RegistrationProducesCanonicalDescriptorsUnderParallelCalls()
    {
        var services = new ServiceCollection();
        var expectedServices = new ServiceCollection();
        expectedServices.AddTcjDependencyInjection(TestAssembly);
        string expected = Canonicalize(expectedServices);

        return StressRunner.RunAsync(
            nameof(RegistrationProducesCanonicalDescriptorsUnderParallelCalls),
            "core",
            _ =>
            {
                services.AddTcjDependencyInjection(TestAssembly);
                return Task.CompletedTask;
            },
            () =>
            {
                Assert.Equal(expected, Canonicalize(services));
                Assert.Single(services.Where(item => item.ServiceType == typeof(IGuidGenerator)));
                Assert.Single(services.Where(item => item.ServiceType == typeof(IDomainEventDispatcher)));
                Assert.Single(services.Where(item => item.ServiceType == typeof(ISingletonProbe)));
                return Task.CompletedTask;
            });
    }

    [Fact]
    public Task AssemblyScanningOrderRemainsDeterministic()
    {
        var expectedServices = new ServiceCollection();
        expectedServices.AddTcjDependencyInjection(options => options.AddAssemblies(new[] { TestAssembly, typeof(IGuidGenerator).Assembly }));
        string expected = Canonicalize(expectedServices);

        return StressRunner.RunAsync(nameof(AssemblyScanningOrderRemainsDeterministic), "core", context =>
        {
            Assembly[] assemblies = context.Iteration % 2 == 0
                ? [TestAssembly, typeof(IGuidGenerator).Assembly]
                : [typeof(IGuidGenerator).Assembly, TestAssembly];
            var services = new ServiceCollection();
            services.AddTcjDependencyInjection(options => options.AddAssemblies(assemblies));
            Assert.Equal(expected, Canonicalize(services));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public Task DuplicateRegistrationProtectionRemainsDeterministic() =>
        StressRunner.RunAsync(nameof(DuplicateRegistrationProtectionRemainsDeterministic), "core", _ =>
        {
            var services = new ServiceCollection();
            services.AddTcjDependencyInjection(TestAssembly);
            services.AddTcjDependencyInjection(TestAssembly);
            Assert.Single(services.Where(item => item.ServiceType == typeof(IGuidGenerator)));
            Assert.Single(services.Where(item => item.ServiceType == typeof(IDomainEventDispatcher)));
            Assert.Single(services.Where(item => item.ServiceType == typeof(ISingletonProbe)));
            return Task.CompletedTask;
        });

    [Fact]
    public async Task TransientServicesAreDistinctUnderConcurrentResolution()
    {
        using ServiceProvider provider = BuildProvider();
        await StressRunner.RunAsync(nameof(TransientServicesAreDistinctUnderConcurrentResolution), "core", _ =>
        {
            ITransientProbe first = provider.GetRequiredService<ITransientProbe>();
            ITransientProbe second = provider.GetRequiredService<ITransientProbe>();
            Assert.NotSame(first, second);
            Assert.NotEqual(first.Id, second.Id);
            return Task.CompletedTask;
        });
    }

    [Fact]
    [Trait("Category", "RequestScope")]
    public async Task ScopedServicesStayWithinOwningScope()
    {
        using ServiceProvider provider = BuildProvider();
        await StressRunner.RunAsync(nameof(ScopedServicesStayWithinOwningScope), "core", _ =>
        {
            using IServiceScope scope = provider.CreateScope();
            IScopedProbe first = scope.ServiceProvider.GetRequiredService<IScopedProbe>();
            IScopedProbe second = scope.ServiceProvider.GetRequiredService<IScopedProbe>();
            Assert.Same(first, second);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task SingletonServicesRemainStableAcrossConcurrentScopes()
    {
        using ServiceProvider provider = BuildProvider();
        Guid expected = provider.GetRequiredService<ISingletonProbe>().Id;
        await StressRunner.RunAsync(nameof(SingletonServicesRemainStableAcrossConcurrentScopes), "core", _ =>
        {
            using IServiceScope scope = provider.CreateScope();
            Assert.Equal(expected, scope.ServiceProvider.GetRequiredService<ISingletonProbe>().Id);
            return Task.CompletedTask;
        });
    }

    [Fact]
    [Trait("Category", "Cancellation")]
    public async Task DisposingIndependentScopesDoesNotLeakState()
    {
        using ServiceProvider provider = BuildProvider();
        await StressRunner.RunAsync(nameof(DisposingIndependentScopesDoesNotLeakState), "core", _ =>
        {
            IServiceScope firstScope = provider.CreateScope();
            using IServiceScope secondScope = provider.CreateScope();
            IDisposalProbe first = firstScope.ServiceProvider.GetRequiredService<IDisposalProbe>();
            IDisposalProbe second = secondScope.ServiceProvider.GetRequiredService<IDisposalProbe>();
            firstScope.Dispose();
            Assert.True(first.IsDisposed);
            Assert.False(second.IsDisposed);
            return Task.CompletedTask;
        });
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddTcjDependencyInjection(TestAssembly);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static string Canonicalize(IServiceCollection services) => string.Join("\n", RelevantDescriptors(services));

    private static string[] RelevantDescriptors(IServiceCollection services) => services
        .Where(descriptor => descriptor.ServiceType.Namespace?.StartsWith("TCJ.Concurrency.Tests", StringComparison.Ordinal) == true ||
                             descriptor.ServiceType == typeof(IGuidGenerator) ||
                             descriptor.ServiceType == typeof(IDomainEventDispatcher) ||
                             descriptor.ServiceType == typeof(TimeProvider))
        .Select(descriptor => $"{descriptor.ServiceType.FullName}|{descriptor.ImplementationType?.FullName ?? "factory"}|{descriptor.Lifetime}")
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();
}
