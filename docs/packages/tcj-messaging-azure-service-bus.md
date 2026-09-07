# TCJ.Messaging.AzureServiceBus

`TCJ.Messaging.AzureServiceBus` is the production Azure Service Bus adapter for the transport-neutral contracts in `TCJ.Messaging`. Azure SDK dependencies stay inside the adapter package.

## Install

```bash
dotnet add package TCJ.Messaging.AzureServiceBus --prerelease
```

The adapter targets `net10.0` and depends on `TCJ.Messaging` plus the official Azure Service Bus SDK. Keep all TCJ runtime packages on the same version.

## Registration

Prefer a `TokenCredential` in production:

```csharp
using Azure.Identity;
using TCJ.Messaging.AzureServiceBus.Extensions;
using TCJ.Messaging.Extensions;

builder.Services.AddTcjMessaging();
builder.Services.AddTcjAzureServiceBus(
    "example.servicebus.windows.net",
    new DefaultAzureCredential());
```

Connection strings are supported for environments that require them but must come from an external secret provider and must never be logged.

## Guarantees

- At-least-once publication when used with TCJ Outbox.
- At-least-once broker delivery with idempotent processing through TCJ Inbox.
- Peek-Lock receive and manual settlement; auto-completion is disabled.
- Queue, topic/subscription, scheduled delivery, TTL, abandon, dead-letter, defer, bounded lock renewal, and broker-aware batch publishing.
- Ordered handling only within one configured Service Bus session; no global ordering or global exactly-once guarantee.
- Broker duplicate detection is time-window bounded and does not replace TCJ Inbox idempotency.

For configuration, topology modes, sessions, retry identity, health, telemetry, testing, and deployment guidance, see [Azure Service Bus messaging](../messaging-azure-service-bus.md).
