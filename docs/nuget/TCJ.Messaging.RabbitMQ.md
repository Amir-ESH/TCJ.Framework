# TCJ.Messaging.RabbitMQ

`TCJ.Messaging.RabbitMQ` is the production RabbitMQ transport adapter for `TCJ.Messaging`. It implements broker publishing, receiving, manual settlement, topology management, recovery, observability, health checks, and the transport bridges used by TCJ transactional Inbox and Outbox processing.

## Install

```bash
dotnet add package TCJ.Messaging.RabbitMQ --prerelease
```

TCJ Framework is currently pre-1.0. Pin the exact preview version used by your application when reproducibility matters.

## Highlights

- RabbitMQ-specific implementation remains isolated from the transport-neutral `TCJ.Messaging` package.
- Persistent publishing with publisher confirms and mandatory-routing failure detection.
- Manual acknowledgement so a delivery is not acknowledged before the committed Inbox outcome allows it.
- Durable direct, topic, and fanout exchange topology with explicit queues and bindings.
- Explicit `Declare`, `ValidateOnly`, and `Disabled` topology modes.
- Bounded prefetch and consumer concurrency.
- Finite TTL/DLX retry topology and terminal dead-letter routing without requiring the delayed-message plugin.
- Automatic connection recovery with bounded connection, publisher-confirm, and shutdown timeouts.
- W3C trace-context propagation, bounded telemetry, readiness health checks, and startup diagnostics.
- At-least-once publication with TCJ Outbox and at-least-once broker delivery with idempotent processing through TCJ Inbox. The adapter does not claim global exactly-once delivery.

## Registration

Register the transport-neutral messaging services first, then add the RabbitMQ adapter:

```csharp
using TCJ.Messaging.Extensions;
using TCJ.Messaging.RabbitMQ.Extensions;

services.AddTcjMessaging();

services.AddTcjRabbitMq(options =>
{
    options.HostName = "rabbitmq.internal";
    options.VirtualHost = "/";
    options.UserName = configuration["RabbitMQ:UserName"]!;
    options.Password = configuration["RabbitMQ:Password"]!;
    options.PrefetchCount = 16;
    options.MaximumConcurrentMessages = 8;
});
```

Load credentials from environment variables, a secret provider, or another protected configuration source. Do not commit production credentials to application settings or source control.

## Topology

Topology is explicit and can be configured with the adapter builder:

```csharp
services.AddTcjRabbitMqTopology(topology =>
{
    topology.AddTopicExchange("orders");
    topology.AddQueue("orders.worker");
    topology.Bind("orders", "orders.worker", "order.*");
});
```

Production deployments should choose topology ownership deliberately. Use validation-only mode when exchanges and queues are managed by infrastructure-as-code or a platform team.

## Delivery guarantees

RabbitMQ delivery is at least once. Publisher confirms protect TCJ Outbox success marking, while TCJ Inbox provides idempotent consumer processing. Redelivery, retries, multiple consumers, and recovery can change observed processing order, so the adapter does not promise global strict ordering or global exactly-once processing.

## Dependencies

`TCJ.Messaging.RabbitMQ` depends on `TCJ.Messaging` and the official RabbitMQ .NET client. RabbitMQ client types are not introduced into the public contracts of transport-neutral TCJ packages.

## Documentation

- [Transport-neutral messaging](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/messaging.md)
- [Messaging adapter authoring](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/messaging-adapter-authoring.md)
- [Transactional Outbox](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/outbox.md)
- [Transactional Inbox](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/inbox.md)
- [Observability](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/observability.md)
- [Health checks](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/health-checks.md)
- [Repository](https://github.com/Amir-ESH/TCJ.Framework)
- [Issues](https://github.com/Amir-ESH/TCJ.Framework/issues)

## License

TCJ Framework is licensed under GNU LGPL v3.0 only (`LGPL-3.0-only`). See the repository license for details.
