using Microsoft.Extensions.DependencyInjection;
using TCJ.Messaging.AzureServiceBus.Extensions;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Publishing;

const string emulator = "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
var services = new ServiceCollection(); services.AddTcjMessaging(); services.AddTcjAzureServiceBus(emulator);
await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
MessagingTransportDescriptor descriptor = provider.GetRequiredService<MessagingTransportDescriptor>();
if (descriptor.Name != "AzureServiceBus" || !descriptor.Capabilities.SupportsPeekLock || !descriptor.Capabilities.SupportsScheduling) return 1;
Console.WriteLine("TCJ.Messaging.AzureServiceBus publisher consumer passed"); return 0;
