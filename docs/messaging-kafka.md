# Apache Kafka messaging transport

`TCJ.Messaging.Kafka` is the Apache Kafka transport adapter for the transport-neutral contracts in `TCJ.Messaging`. Kafka SDK types remain isolated inside the adapter package.

## Installation and registration

```bash
dotnet add package TCJ.Messaging --prerelease
dotnet add package TCJ.Messaging.Kafka --prerelease
```

`AddTcjMessaging()` is required before `AddTcjKafka()`:

```csharp
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Kafka.Extensions;

services.AddTcjMessaging();
services.AddTcjKafka(options =>
{
    options.BootstrapServers = "kafka-1:9093,kafka-2:9093";
    options.ClientId = "orders-api";
    options.MaximumConcurrentPartitions = 8;
    options.MaximumBufferedMessages = 64;
});
```

The adapter registers the existing transport-level publisher, batch publisher, receiver, consumer runner, health probe, startup validator, and `MessagingTransportDescriptor`. It does not register another application-level `IMessagePublisher`.

## Delivery guarantees

Publishing through TCJ Outbox provides **at-least-once logical publication**. The adapter waits for the final Kafka delivery result before reporting `Published`, so Outbox success is not recorded on local client enqueue alone. Kafka producer idempotence reduces duplicate writes caused by producer retries, but it does not replace TCJ Outbox.

Consumption is **at-least-once Kafka delivery with idempotent database-side processing through TCJ Inbox**. A Kafka offset can fail to commit after the Inbox transaction has committed. In that crash window Kafka may redeliver the record; Inbox duplicate detection prevents a second business effect and the duplicate can then advance the offset safely.

The advertised ordering guarantee is **sequential application processing within one partition**. Different partitions may be processed concurrently up to `MaximumConcurrentPartitions`. There is no global ordering and no ordering guarantee across partitions.

The initial adapter does not claim Kafka transactions as a TCJ capability: `SupportsTransactions = false`. Kafka producer idempotence or broker transactions do not provide exactly-once processing across Kafka and an application database.

## Topic and key mapping

Topic names are explicit transport destinations and are validated independently from CLR type names. The default is `tcj.events`.

Kafka key selection is deterministic:

| TCJ metadata | Kafka key |
| --- | --- |
| `PartitionKey` only | `PartitionKey` |
| `OrderingKey` only | `OrderingKey` |
| both, equal | that shared value |
| both, different | validation failure |

Keys are not emitted as telemetry dimensions or log fields. Changing topic names, partition counts, key algorithms, or consumer-group names is an operational migration because it can alter routing, ordering, ownership, and replay behavior.

## Stable message identity and headers

The stable TCJ logical `MessageId` is stored in the `tcj-message-id` Kafka header and remains unchanged across producer retry, broker redelivery, retry-topic publication, dead-letter publication, and Inbox duplicate detection. Inbound records without the required TCJ message ID fail rather than silently receiving a random identifier.

Logical type, schema version, content type, correlation ID, causation ID, partition key, and ordering key are preserved. Application headers pass through the existing `MessagingHeaderPolicy`, including its allowlist, bounds, forbidden-secret filtering, and W3C trace-context validation.

## Producer configuration and retry ownership

Production-safe invariants are fail-closed:

```text
EnableIdempotence = true
AcknowledgementMode = All
EnableAutoCommit = false
EnableAutoOffsetStore = false
```

Kafka-client producer retry is short and bounded by `ProducerRetryCount` (at least 1 while idempotence is enabled), `ProducerRetryBackoff`, and `PublishTimeout`. Durable publication retry remains owned by TCJ Outbox. The adapter does not wrap the client in an unbounded retry loop.

One producer is lazily initialized and reused thread-safely. Registration does not open a broker connection. Publish, producer flush, and shutdown are bounded.

## Consumer groups and manual offsets

`ReceiveContext.Subscription` is the authoritative Kafka consumer-group ID. It is required for Kafka receives. Automatic commit and automatic offset store are disabled.

The fundamental offset invariant is:

> No Kafka offset is committed unless every earlier delivered offset in the same partition has reached a durable Inbox outcome that permits settlement.

For example, if offsets 10 and 11 are complete, 12 is unresolved, and 13 is complete, the committed Kafka position can advance only to 12, never to 14. A per-partition coordinator tracks delivered and completed offsets and advances only a contiguous completed prefix. Independent partitions advance independently.

Kafka consumer operations (`Consume`, `Commit`, `Pause`, `Resume`, `Seek`, assignment/revocation operations, and `Close`) are serialized through the consumer-owner loop. Handler tasks never call the Kafka consumer concurrently.

## Rebalance behavior

Assignment creates a new partition generation. Settlement handles include that generation so a handle from an old owner becomes stale after revocation and cannot commit.

On revocation, new dispatch for the partition stops, only the highest contiguous safe position is eligible for commit, unresolved work remains uncommitted, stale settlements are invalidated, and the new owner can redeliver unresolved records. If durable Inbox work is redelivered because an offset commit failed, Inbox idempotency makes the recovery safe.

`MaxPollInterval` is explicit and bounded. Polling remains in the owner loop rather than behind long-running application handlers, so handler duration does not directly stop Kafka polling.

## Backpressure

The receiver uses a bounded global in-flight limit and the runner uses bounded partition scheduling. When capacity is exhausted, assigned partitions are paused while the consumer owner continues the polling lifecycle; they resume when capacity returns. A record surfaced while paused is not discarded: the owner seeks back to its offset so it remains deliverable.

## Settlement, retry, dead letter, abandon, and defer

`CompleteAsync()` marks the record complete in the offset coordinator; worker code does not commit Kafka offsets directly.

Immediate retry is a durable retry-topic publication. The adapter publishes the cloned record to `<source>.retry`, waits for Kafka delivery success, and only then marks the source offset complete. Retry attempts are bounded by `MaximumProcessingAttempts`; once exhausted, the message is routed to `<source>.dead`. If retry or dead-letter publication fails, the source offset remains unresolved.

A positive in-process retry delay is intentionally unsupported because sleeping while the only copy is in memory is not a durable delayed-retry mechanism. Deployments that require delayed retry should use an external durable retry-topic scheduling strategy.

Kafka has no queue-style abandon or Service Bus-style defer. `AbandonAsync()` and `DeferAsync()` therefore fail explicitly through `MessagingCapabilityException`; `SupportsDefer = false`.

## Security

TCJ-owned configuration supports plaintext, TLS, SASL/PLAIN, SCRAM-SHA-256, and SCRAM-SHA-512 combinations supported by the selected client. Keep credentials in a secret provider and use least-privilege broker ACLs.

Passwords, credentials, certificate secrets, payloads, raw headers, message IDs, and Kafka keys must not be emitted to logs, traces, metrics, health responses, workflow summaries, or verifier output. Health and startup failures report bounded classifications rather than connection strings.

## Topology ownership

`TopologyMode` is explicit:

- `Disabled` (default): no Kafka admin permission is required solely for topology ownership.
- `ValidateOnly`: configured topics are checked but never created or modified.
- `Declare`: missing configured topics are created using explicit partition/replication settings.

Externally managed Kafka clusters should normally use infrastructure-as-code with `Disabled` or `ValidateOnly`. The adapter never silently changes partition counts or other externally managed topic properties.

## Observability and health

Kafka activities cover publication, consumption/processing, offset commit, assignment/revocation, pause/resume, retry publication, and dead-letter publication. Metrics cover publish/process outcomes, retries, dead letters, commit outcomes, and rebalances using bounded-cardinality dimensions.

Liveness remains independent from Kafka availability. Kafka readiness is a bounded connectivity check and returns sanitized status. Startup validation verifies registration, safe producer/consumer defaults, bounds, consumer-group requirements, capabilities, and topology policy without disclosing secrets.

## Graceful shutdown

Shutdown stops new dispatch, pauses owned partitions, drains active work within `ShutdownTimeout`, accepts safe settlement completions, commits only contiguous completed positions where possible, leaves unresolved offsets uncommitted, flushes the producer boundedly, closes the consumer, and disposes resources. A commit failure never converts already committed Inbox work into a business retry; it remains safe for Kafka redelivery.

## Native AOT and trimming

The initial adapter is deliberately conservative: the package declares `IsAotCompatible=false` and the repository AOT policy records Kafka as unsupported until packed-consumer evidence proves a stronger claim. This does not relax the requirement to keep TCJ registration reflection-light or to prevent `Confluent.Kafka` types from leaking into public/protected TCJ APIs.

## Integration testing

Repository integration tests use the pinned `confluentinc/cp-kafka:7.5.12` image in KRaft mode through `Testcontainers.Kafka`. The dedicated `Kafka transport` workflow validates the contract, package isolation, producer semantics, partition/offset correctness, retry/DLT behavior, and real-broker scenarios.

Run the static contract verifier first:

```bash
python3 eng/verify-kafka.py validate-config
```

Then run the Kafka tests on a machine with .NET 10 and Docker:

```bash
dotnet test tests/TCJ.Messaging.Kafka.Tests/TCJ.Messaging.Kafka.Tests.csproj --configuration Release
```

## Migration considerations

Introducing Kafka is additive, but routing configuration is operationally significant. Before changing an existing deployment, explicitly plan topic creation/ownership, partition count, key mapping, consumer-group names, offset-reset policy, retained offsets, retry/DLT topic retention, ACLs, and replay procedure. Changing a key or partition topology can change which partition receives future records and therefore changes the scope of ordering.
