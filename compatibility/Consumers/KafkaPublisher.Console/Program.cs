using Microsoft.Extensions.DependencyInjection;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Kafka.Extensions;
using TCJ.Messaging.Publishing;

var services = new ServiceCollection();
services.AddTcjMessaging();
services.AddTcjKafka(options => options.BootstrapServers = "localhost:9092");
await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
MessagingTransportDescriptor descriptor = provider.GetRequiredService<MessagingTransportDescriptor>();
if (descriptor.Name != "Kafka" || !descriptor.Capabilities.SupportsPartitioning ||
    descriptor.Capabilities.SupportsTransactions || descriptor.Capabilities.SupportsDefer)
    return 1;
Console.WriteLine("TCJ.Messaging.Kafka publisher consumer passed");
return 0;
