# Transport-neutral messaging

`TCJ.Messaging` defines the broker-neutral messaging boundary for TCJ Framework. It contains envelopes, serialization contracts, publishing and receiving abstractions, settlement semantics, topology naming, bounded consumer execution, diagnostics, health checks, and integration bridges for the existing transactional Outbox and Inbox.

The package intentionally contains **no production broker SDK**. RabbitMQ, Azure Service Bus, Kafka, SQS, NATS, and other transports belong in future adapter packages that implement the contracts described in [Messaging adapter authoring](messaging-adapter-authoring.md).

## Delivery semantics

TCJ messaging is designed for **at-least-once** transport delivery. It does not claim global exactly-once delivery. Application handlers should remain idempotent, and a stable `MessageId` must be preserved across publish retries, broker redelivery, Inbox processing, and replay.

`MessageType` is a logical compatibility contract such as `order.submitted`; it is not a CLR assembly-qualified name. `MessageVersion` is a separate positive schema version. Breaking wire-shape changes require an explicit version change and migration guidance.

## Envelopes

`MessageEnvelope<TMessage>` is the typed application envelope. `TransportMessageEnvelope` is the serialized adapter boundary and uses `ReadOnlyMemory<byte>` for the body. Both carry:

- stable `MessageId`;
- logical `MessageType` and positive `MessageVersion`;
- UTC creation time;
- optional `CorrelationId` and `CausationId`;
- optional `PartitionKey` and `OrderingKey` hints;
- a bounded, copied header dictionary.

The framework does not promise global ordering. Ordering and partition behavior depend on the selected adapter capability declaration.

## Serialization and schema evolution

The default serializer is `System.Text.Json`, but only explicitly registered `JsonTypeInfo` metadata is used. Wire data is never allowed to select an arbitrary CLR type. Unknown content types are rejected rather than silently interpreted as JSON.

Register an explicit contract:

```csharp
services.AddTcjMessaging();
services.AddTcjMessage(
    "order.submitted",
    1,
    AppJsonContext.Default.OrderSubmitted);
```

Schema upcasters are explicit `IMessageUpcaster` registrations. A missing or ambiguous upcast chain fails closed.

## Header policy and trace context

Headers are allowlist-based and bounded by count, key length, value length, and aggregate byte size. Security-sensitive names such as `authorization`, `cookie`, `set-cookie`, API keys, access/refresh tokens, passwords, and connection strings are not propagated by default.

Framework metadata headers include logical message metadata, correlation/causation, content type, and W3C `traceparent` / `tracestate`. Malformed `traceparent` is ignored safely; orphan `tracestate` is dropped. Payloads and credential values are not logged by the messaging diagnostics layer.

## Publishing

`IMessagePublisher` returns a stable `PublishResult`. Outcomes distinguish:

- `Published` / `Accepted`;
- `TransientFailure`;
- `PermanentFailure`;
- `Canceled`;
- `TimedOut`;
- `UnsupportedCapability`.

`IMessageBatchPublisher` keeps results index-aligned with input messages. Partial success is represented explicitly; successful items must not be blindly republished by retry ownership outside the adapter.

Publish timeout is bounded by `TcjMessagingOptions.PublishTimeout` and uses the registered `TimeProvider`, which keeps timeout behavior deterministic in tests.

## Capability declarations

Each adapter registers exactly one `MessagingTransportDescriptor` with `MessagingTransportCapabilities`. Capabilities include batch publishing, scheduling, TTL, dead-lettering, deferral, ordering, partitioning, transactions, peek-lock behavior, and adapter limits.

A caller must not assume an unsupported feature. Unsupported scheduling, TTL, partitioning, ordering, batch, settlement, or similar behavior fails explicitly instead of being silently ignored.

## Transactional Outbox bridge

Outbox integration is **opt-in**:

```csharp
services.AddTcjMessaging();
services.AddTcjMessage("order.submitted", 1, AppJsonContext.Default.OrderSubmitted);
services.AddMyMessagingAdapter();
services.AddTcjMessagingOutboxBridge();
```

Call `AddTcjMessagingOutboxBridge()` only after the application has registered `IDomainEventDispatcher` and the existing Outbox infrastructure.

During an Outbox delivery, the bridge:

1. reads the current `IOutboxMessageContextAccessor`;
2. preserves the persisted Outbox `MessageId` as the transport message ID;
3. preserves correlation and causation metadata;
4. resolves an explicitly registered message contract;
5. publishes through `IMessagePublisher`;
6. returns only after a successful transport outcome.

The existing Outbox processor therefore marks a record processed only after successful publish. A retryable transport result is classified through TCJ's existing `ITransientFailureClassifier` extension point; permanent failures remain permanent. Outside an Outbox delivery the original domain-event dispatcher behavior is unchanged.

## Transactional Inbox bridge

Transport receive integration flows through `InboxTransportBridge`:

```text
transport receive
-> validate/filter transport envelope
-> transactional Inbox pipeline
-> committed InboxHandlingResult
-> transport settlement
```

Settlement occurs **after** the Inbox pipeline returns its committed outcome. The default mapping is:

| Inbox outcome | Transport settlement |
| --- | --- |
| `Acknowledge` | `Complete` |
| `IgnoreDuplicate` | `Complete` |
| `Retry` | `Retry` or `Abandon` when required by adapter capabilities |
| `DeadLetter` | `DeadLetter`, or `Abandon` when dead-letter is unsupported |
| cancellation | no false completion |

This ordering prevents transport acknowledgement from preceding the durable Inbox transaction.

## Bounded consumers and graceful shutdown

`MessageConsumerRunner` enforces `MaximumConcurrentMessages`. The selected adapter is also required to keep its receive buffer bounded; the in-memory adapter uses bounded channels and applies backpressure when its global capacity is full.

On shutdown the runner stops receiving new deliveries, gives active work up to `ShutdownTimeout` to finish, and only then cancels remaining processing. This is a graceful shutdown boundary, not an arbitrary delay or retry mechanism.

## In-memory adapter

`AddTcjInMemoryMessaging()` registers the reference adapter used by contract tests and local development. It supports deterministic failure plans, bounded buffering, duplicate injection, retry/redelivery, dead-letter snapshots, deterministic `TimeProvider`, and health probing.

The in-memory adapter is **non-durable**. It is not a production broker, does not survive process termination, and must not be used as evidence of durable cross-process delivery.

## Diagnostics

Activities are emitted from the `TCJ.Messaging` activity source for publish, receive, settle, deserialize, and consumer execution. Metrics cover published/received/completed/retried/dead-lettered messages, publish and processing duration, and active consumers.

Metric dimensions are deliberately bounded to stable framework dimensions such as adapter name, outcome, and logical message contract. Raw payloads, arbitrary headers, exception messages, destination names supplied by untrusted input, and message IDs must not become unbounded metric labels.

Health checks are registered explicitly through `AddTcjMessagingHealthChecks()`:

- `tcj.messaging.transport`;
- `tcj.messaging.publisher`;
- `tcj.messaging.consumer`;
- `tcj.messaging.topology`.

Startup validation fails closed when adapter registrations, limits, consumer dependencies, or topology contracts are invalid.

## Verification

The repository gate is:

```bash
python3 eng/verify-messaging.py validate-config

dotnet test tests/TCJ.Messaging.Tests/TCJ.Messaging.Tests.csproj \
  --configuration Release \
  --logger "trx;LogFileName=messaging-contracts.trx" \
  --results-directory TestResults/Messaging

dotnet test tests/TCJ.Messaging.ConformanceTests/TCJ.Messaging.ConformanceTests.csproj \
  --configuration Release \
  --logger "trx;LogFileName=messaging-conformance.trx" \
  --results-directory TestResults/Messaging

python3 eng/verify-messaging.py verify \
  --results TestResults/Messaging \
  --output artifacts/messaging
```

Generated evidence under `TestResults/Messaging/` and `artifacts/messaging/` is CI output and is not source-controlled.
