using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.Identifiers;
using TCJ.DependencyInjection.Extensions;
using TCJ.DependencyInjection.Lifetimes;

namespace TcjUpgrade.DependencyInjectionConsumer;

public static class Program
{
    public static async Task Main()
    {
        var services = new ServiceCollection();
        services.AddTcjDependencyInjection(typeof(Program).Assembly);
        services.AddTcjDependencyInjection(typeof(Program).Assembly);

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        ITransientService transient1 = provider.GetRequiredService<ITransientService>();
        ITransientService transient2 = provider.GetRequiredService<ITransientService>();
        using IServiceScope scope1 = provider.CreateScope();
        IScopedService scoped1a = scope1.ServiceProvider.GetRequiredService<IScopedService>();
        IScopedService scoped1b = scope1.ServiceProvider.GetRequiredService<IScopedService>();
        using IServiceScope scope2 = provider.CreateScope();
        IScopedService scoped2 = scope2.ServiceProvider.GetRequiredService<IScopedService>();
        ISingletonService singleton1 = provider.GetRequiredService<ISingletonService>();
        ISingletonService singleton2 = provider.GetRequiredService<ISingletonService>();

        var behavior = new
        {
            schemaVersion = 1,
            scenario = "DependencyInjectionConsumer",
            checks = new
            {
                transientDistinct = !ReferenceEquals(transient1, transient2),
                scopedStable = ReferenceEquals(scoped1a, scoped1b),
                scopeIsolated = !ReferenceEquals(scoped1a, scoped2),
                singletonStable = ReferenceEquals(singleton1, singleton2),
                selfRegistered = provider.GetRequiredService<SelfTransientService>() is not null,
                guidGeneratorResolved = provider.GetRequiredService<IGuidGenerator>() is not null,
            },
        };

        await WriteBehaviorAsync(behavior);
        Console.WriteLine("TCJ.DependencyInjection upgrade scenario passed");
    }

    private static async Task WriteBehaviorAsync<T>(T value)
    {
        string path = Environment.GetEnvironmentVariable("TCJ_UPGRADE_BEHAVIOR_PATH")
            ?? throw new InvalidOperationException("TCJ_UPGRADE_BEHAVIOR_PATH is required.");
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Behavior path has no directory."));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public interface ITransientService { Guid InstanceId { get; } }
public sealed class TransientService : ITransientService, ITransientDependency { public Guid InstanceId { get; } = Guid.NewGuid(); }
public interface IScopedService { Guid InstanceId { get; } }
public sealed class ScopedService : IScopedService, IScopedDependency { public Guid InstanceId { get; } = Guid.NewGuid(); }
public interface ISingletonService { Guid InstanceId { get; } }
public sealed class SingletonService : ISingletonService, ISingletonDependency { public Guid InstanceId { get; } = Guid.NewGuid(); }
public sealed class SelfTransientService : ISelfTransientDependency { }
