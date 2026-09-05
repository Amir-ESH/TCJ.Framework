using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Messaging.AzureServiceBus.Extensions;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Publishing;

string fullyQualifiedNamespace = Environment.GetEnvironmentVariable("TCJ_SERVICE_BUS_NAMESPACE")
    ?? throw new InvalidOperationException("Set TCJ_SERVICE_BUS_NAMESPACE, for example example.servicebus.windows.net.");

var services = new ServiceCollection();
services.AddTcjMessaging();
services.AddTcjAzureServiceBus(
    fullyQualifiedNamespace,
    new DefaultAzureCredential(),
    options =>
    {
        options.PrefetchCount = 32;
        options.MaximumConcurrentMessages = 8;
        options.MaximumConcurrentSessions = 4;
        options.MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5);
        options.ReadinessDestination = "orders";
    });

await using ServiceProvider provider = services.BuildServiceProvider(
    new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

MessagingTransportDescriptor descriptor = provider.GetRequiredService<MessagingTransportDescriptor>();
Console.WriteLine($"Registered {descriptor.Name} {descriptor.Version}. Peek-Lock={descriptor.Capabilities.SupportsPeekLock}.");
