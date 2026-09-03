# TCJ.Messaging

`TCJ.Messaging` is the broker-neutral messaging package for TCJ Framework. It defines immutable envelopes, explicit publish/receive/settlement contracts, logical message type and schema-version registration, bounded header and payload policies, topology naming, transport capabilities, Inbox/Outbox bridges, health checks, telemetry, and an in-memory conformance transport.

## Install

```bash
dotnet add package TCJ.Messaging --prerelease
```

TCJ Framework is currently pre-1.0. Pin the exact preview version used by your application when reproducibility matters.

## Highlights

- No RabbitMQ, Azure Service Bus, Kafka, SQS, NATS, or other broker SDK dependency.
- Stable message IDs, logical message types, schema versions, correlation, causation, partition and ordering hints.
- Explicit `JsonTypeInfo<TMessage>` registration for trimming/AOT-safe JSON serialization.
- Allowlist-based transport headers with sensitive-header filtering.
- Explicit publish outcomes and failure categories.
- Bounded batch, receive, concurrency, timeout, and graceful-shutdown behavior.
- Transactional Inbox receive settlement and opt-in Outbox publishing bridge.
- Adapter capability declarations and deterministic topology naming.
- Activities, bounded-cardinality metrics, startup diagnostics, and health checks.
- Bounded non-durable in-memory adapter plus reusable conformance suite for adapter authors.

## Minimal registration

```csharp
using TCJ.Messaging.Extensions;

services.AddTcjMessaging();
```

Register each wire contract explicitly with a logical name, positive version, and source-generated or otherwise explicit JSON metadata.

## Dependencies

`TCJ.Messaging` depends on `TCJ.Core`. It does not depend on a production broker SDK.

## In-memory adapter

`AddTcjInMemoryMessaging()` is intended for tests and local development only. It is non-durable and must not be treated as a production message broker.

## Documentation

- [TCJ.Messaging package documentation](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/packages/tcj-messaging.md)
- [Transport-neutral messaging](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/messaging.md)
- [Messaging adapter authoring](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/messaging-adapter-authoring.md)
- [Transactional Outbox](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/outbox.md)
- [Transactional Inbox](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/inbox.md)
- [Native AOT and trimming](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/guides/native-aot-and-trimming.md)
- [Repository](https://github.com/Amir-ESH/TCJ.Framework)
- [Issues](https://github.com/Amir-ESH/TCJ.Framework/issues)

## License

TCJ Framework is licensed under GNU LGPL v3.0 only (`LGPL-3.0-only`). See the repository license for details.
