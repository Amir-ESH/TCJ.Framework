using Microsoft.Extensions.DependencyInjection;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;
using TCJ.Messaging.RabbitMQ.Extensions;
using TCJ.Messaging.RabbitMQ.Topology;

var services = new ServiceCollection();
services.AddTcjMessaging();
services.AddTcjRabbitMq(o => o.TopologyMode = RabbitMqTopologyMode.Disabled);
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
var descriptor = provider.GetRequiredService<MessagingTransportDescriptor>();
if (descriptor.Name != "RabbitMQ" || descriptor.Capabilities.SupportsBatchPublish ||
    descriptor.Capabilities.SupportsDefer || !descriptor.Capabilities.SupportsTimeToLive ||
    descriptor.Capabilities.OrderingGuarantee != MessagingOrderingGuarantee.BestEffort) return 1;
_ = provider.GetRequiredService<IMessagePublisher>();
_ = provider.GetRequiredService<IMessageReceiver>();
Console.WriteLine("TCJ.Messaging.RabbitMQ RabbitMqInboxOutbox.Worker consumer passed");
return 0;
