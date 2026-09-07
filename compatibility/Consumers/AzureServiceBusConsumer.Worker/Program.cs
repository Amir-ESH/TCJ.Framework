using Microsoft.Extensions.DependencyInjection;
using TCJ.Messaging.AzureServiceBus.Extensions;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Receiving;

const string emulator = "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
var services = new ServiceCollection(); services.AddTcjMessaging(); services.AddTcjAzureServiceBus(emulator);
await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
if (provider.GetRequiredService<IMessageReceiver>() is null) return 1;
Console.WriteLine("TCJ.Messaging.AzureServiceBus consumer worker passed"); return 0;
