# TCJ.Messaging.Kafka

`TCJ.Messaging.Kafka` is the optional Apache Kafka transport adapter for `TCJ.Messaging`. It provides confirmed publishing, bounded batch publishing, keyed partitioning, consumer groups, manual contiguous offset progression, per-partition ordering, retry/dead-letter topics, observability, readiness, topology validation/declaration, and Inbox/Outbox interoperability.

## Install

```bash
dotnet add package TCJ.Messaging.Kafka --prerelease
```

## Registration

```csharp
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Kafka.Extensions;

services.AddTcjMessaging();
services.AddTcjKafka(options =>
{
    options.BootstrapServers = "localhost:9092";
    options.ClientId = "orders-api";
});
```

`AddTcjMessaging()` must be registered first. The adapter depends directly only on `TCJ.Messaging`; Kafka SDK types do not become part of neutral TCJ APIs.

## Guarantees

Publication through Outbox is at least once and succeeds only after Kafka confirms delivery. Consumption through Inbox is at least-once Kafka delivery with idempotent database-side processing. Application ordering is sequential within one partition only. The initial adapter has `SupportsTransactions = false` and `SupportsDefer = false`; producer idempotence does not replace Inbox or Outbox and no cross-system exactly-once guarantee is claimed.

## Documentation

See [Apache Kafka messaging transport](../messaging-kafka.md) for offset invariants, rebalance behavior, retry/DLT semantics, TLS/SASL, topology ownership, telemetry, health, AOT status, and migration guidance.

## License

TCJ Framework is licensed under GNU LGPL v3.0 only (`LGPL-3.0-only`).
