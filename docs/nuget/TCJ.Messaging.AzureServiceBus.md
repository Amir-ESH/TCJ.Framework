# TCJ.Messaging.AzureServiceBus

`TCJ.Messaging.AzureServiceBus` is the Azure Service Bus transport adapter for `TCJ.Messaging`. It provides queue/topic/subscription publishing and receiving, Peek-Lock manual settlement, scheduling, TTL, batch publishing, sessions, bounded lock renewal, topology validation/declaration, observability, health checks, and TCJ Inbox/Outbox transport integration.

## Install

```bash
dotnet add package TCJ.Messaging.AzureServiceBus --prerelease
```

The package depends on `TCJ.Messaging` and the official Azure Service Bus SDK. Azure SDK types are not introduced into transport-neutral TCJ packages.

## Registration

Token credentials are preferred for production:

```csharp
using Azure.Identity;
using TCJ.Messaging.AzureServiceBus.Extensions;
using TCJ.Messaging.Extensions;

services.AddTcjMessaging();
services.AddTcjAzureServiceBus(
    "example.servicebus.windows.net",
    new DefaultAzureCredential());
```

A connection-string overload is available where required. Never commit production credentials.

## Delivery guarantees

The adapter uses Peek-Lock and manual completion. Publication through TCJ Outbox is at least once; broker delivery is at least once with idempotent processing through TCJ Inbox. Service Bus duplicate detection does not replace Inbox and no global exactly-once guarantee is claimed. Session ordering is scoped to a single session.

## Documentation

- [Azure Service Bus transport](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/messaging-azure-service-bus.md)
- [Transport-neutral messaging](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/messaging.md)
- [Transactional Outbox](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/outbox.md)
- [Transactional Inbox](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/inbox.md)
- [Repository](https://github.com/Amir-ESH/TCJ.Framework)

## License

TCJ Framework is licensed under GNU LGPL v3.0 only (`LGPL-3.0-only`).

## Partitioning

Queue/topic partitioning is an explicit topology expectation via `EnablePartitioning`. The local emulator does not support partitioned entities; validate this option against Azure in protected integration infrastructure. When both `OrderingKey` and `PartitionKey` are present, their values must match.
