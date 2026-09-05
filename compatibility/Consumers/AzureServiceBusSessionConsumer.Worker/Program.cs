using Microsoft.Extensions.DependencyInjection;
using TCJ.Messaging.AzureServiceBus.Configuration;
using TCJ.Messaging.AzureServiceBus.Extensions;
using TCJ.Messaging.AzureServiceBus.Topology;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Publishing;

const string emulator = "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
var services = new ServiceCollection(); services.AddTcjMessaging(); services.AddTcjAzureServiceBus(emulator, options =>
{
    options.MaximumConcurrentSessions = 4; options.MaximumConcurrentCallsPerSession = 1; options.TopologyMode = AzureServiceBusTopologyMode.Disabled;
    options.Topology.Queues.Add(new AzureServiceBusQueueOptions { Name = "orders-session", RequiresSession = true });
});
await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
if (provider.GetRequiredService<MessagingTransportDescriptor>().Capabilities.OrderingGuarantee != MessagingOrderingGuarantee.PerSession) return 1;
Console.WriteLine("TCJ.Messaging.AzureServiceBus session consumer worker passed"); return 0;
