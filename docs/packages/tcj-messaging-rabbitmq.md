# TCJ.Messaging.RabbitMQ

`TCJ.Messaging.RabbitMQ` is the production RabbitMQ transport adapter for the broker-neutral contracts in `TCJ.Messaging`. RabbitMQ-specific client types remain inside the adapter package and do not become dependencies of the neutral messaging package.

## Install

```bash
dotnet add package TCJ.Messaging.RabbitMQ --prerelease
```

- **Target framework:** `net10.0`
- **Primary TCJ dependency:** `TCJ.Messaging`
- **Broker SDK:** official `RabbitMQ.Client`
- **Main namespaces:** `TCJ.Messaging.RabbitMQ.Configuration`, `TCJ.Messaging.RabbitMQ.Extensions`, `TCJ.Messaging.RabbitMQ.Topology`
- **Primary entry points:** `AddTcjRabbitMq`, `AddTcjRabbitMqTopology`, and `AddTcjRabbitMqHealthChecks`

## Registration

Register `TCJ.Messaging` before selecting the RabbitMQ adapter:

```csharp
using TCJ.Messaging.Extensions;
using TCJ.Messaging.RabbitMQ.Extensions;

builder.Services.AddTcjMessaging();

builder.Services.AddTcjRabbitMq(options =>
{
    options.HostName = "rabbitmq.internal";
    options.VirtualHost = "/";
    options.UserName = builder.Configuration["RabbitMQ:UserName"]!;
    options.Password = builder.Configuration["RabbitMQ:Password"]!;
    options.PrefetchCount = 16;
    options.MaximumConcurrentMessages = 8;
});
```

Production credentials belong in environment configuration or a secret provider. They must not be committed to source control or emitted through diagnostics.

## Publishing and Outbox behavior

The adapter publishes persistent messages with publisher confirmations enabled. Mandatory publishing detects messages that cannot be routed. TCJ Outbox processing treats the transport publish as successful only after the RabbitMQ publish operation completes successfully; nack, return/unroutable, connection loss, and confirm timeout remain failures and do not authorize an Outbox success mark.

The resulting publication guarantee is at least once when used with TCJ Outbox. The package does not claim global exactly-once publication or processing.

## Receiving and settlement

Consumers use manual acknowledgement. RabbitMQ delivery is mapped to the transport-neutral receive contract and then to the TCJ Inbox bridge. A broker acknowledgement is issued only after the Inbox outcome allows completion.

Prefetch and maximum concurrent processing are bounded configuration values. Each message handler runs in its own dependency-injection scope.

## Topology

Explicit topology supports durable direct, topic, and fanout exchanges, queues, bindings, and finite retry/dead-letter paths.

```csharp
builder.Services.AddTcjRabbitMqTopology(topology =>
{
    topology.AddTopicExchange("orders");
    topology.AddQueue("orders.worker");
    topology.Bind("orders", "orders.worker", "order.*");
});
```

The adapter exposes three topology modes:

- `Declare` creates configured topology idempotently and fails on incompatible declarations.
- `ValidateOnly` validates expected broker topology without intentionally creating missing infrastructure.
- `Disabled` leaves topology ownership to external infrastructure.

Retry delay uses standard RabbitMQ TTL/dead-letter features and does not require the delayed-message plugin. Retry topology is finite so poison messages can reach a terminal dead-letter destination instead of entering an unbounded hot loop.

## Recovery, shutdown, and ordering

RabbitMQ automatic connection recovery can be enabled. Connection establishment, publisher confirmation, recovery interval, and graceful shutdown are bounded by configuration.

RabbitMQ can preserve queue order in simple cases, but redelivery, retries, multiple consumers, recovery, and priority can alter observed processing order. The adapter declares best-effort ordering rather than a global strict-order guarantee.

## Observability and health

The adapter provides RabbitMQ-specific activities, bounded-cardinality metrics, startup diagnostics, and readiness health probes. Payloads, credentials, connection strings, and raw sensitive headers are not intended telemetry dimensions.

RabbitMQ readiness may depend on broker connectivity and expected topology. Dependency-independent liveness should remain separate from broker readiness.

## Native AOT and trimming

`TCJ.Messaging.RabbitMQ` currently declares `IsAotCompatible=false`. RabbitMQ is an upstream runtime dependency, so packed-consumer Native AOT compatibility must be evidenced independently before the package can be promoted to a stronger AOT support classification.

See [Native AOT and trimming compatibility](../guides/native-aot-and-trimming.md).

## Related documentation

See [transport-neutral messaging](../messaging.md), [messaging adapter authoring](../messaging-adapter-authoring.md), [transactional Outbox](../outbox.md), [transactional Inbox](../inbox.md), [observability](../observability.md), and [health checks](../health-checks.md).

Related package: [TCJ.Messaging](tcj-messaging.md).
