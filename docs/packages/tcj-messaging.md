# TCJ.Messaging

`TCJ.Messaging` provides broker-neutral messaging contracts that connect TCJ transactional Outbox and Inbox processing to external transports without taking a dependency on a broker SDK.

## Install

```bash
dotnet add package TCJ.Messaging --prerelease
```

- **Target framework:** `net10.0`
- **Primary dependency:** `TCJ.Core`
- **Broker SDK dependencies:** none
- **Main namespaces:** `TCJ.Messaging.Envelopes`, `TCJ.Messaging.Publishing`, `TCJ.Messaging.Receiving`, `TCJ.Messaging.Serialization`, `TCJ.Messaging.Topology`
- **Primary entry points:** `AddTcjMessaging`, `AddTcjMessage<TMessage>`, `IMessagePublisher`, `IMessageReceiver`, and `IMessageConsumerRunner`

## Registration

```csharp
builder.Services.AddTcjMessaging(options =>
{
    options.MaximumPayloadBytes = 1024 * 1024;
    options.MaximumHeaderBytes = 16 * 1024;
    options.MaximumConsumerConcurrency = 8;
});
```

Message contracts are registered explicitly with stable logical names, positive schema versions, and `JsonTypeInfo<TMessage>` metadata:

```csharp
builder.Services.AddTcjMessage(
    "order.submitted",
    1,
    MessagingJsonContext.Default.OrderSubmitted);
```

Logical message names and schema versions are wire compatibility contracts. Do not use assembly-qualified CLR type names as broker contracts.

## Publishing

`IMessagePublisher` validates the envelope, capability requirements, destination, payload/header limits, and transport-safe headers before invoking the selected adapter. A message ID is created before durable Outbox persistence and must remain unchanged across retries and broker redelivery.

Batch publishing reports one result for every input message. Partial success is preserved so successful messages are not blindly republished.

## Receiving and Inbox settlement

The transport receive bridge validates the raw envelope, filters inbound headers, invokes the transactional Inbox pipeline, and settles only after the Inbox result is known.

The default mapping is:

| Inbox outcome | Transport settlement |
| --- | --- |
| Acknowledge | Complete |
| IgnoreDuplicate | Complete |
| Retry | Retry, or Abandon when retry settlement is unsupported |
| DeadLetter | DeadLetter, or Abandon when dead-lettering is unsupported |
| Canceled | No successful completion |

A transport acknowledgement never precedes the Inbox persistence/transaction boundary represented by the returned Inbox outcome.

## Outbox bridge

`AddTcjMessagingOutboxBridge()` explicitly decorates the registered `IDomainEventDispatcher`. It is opt-in and preserves the existing dispatcher outside an active Outbox delivery. Inside Outbox processing it publishes through `IMessagePublisher`, preserves message/correlation/causation identity, and returns success only after the transport reports a successful publish outcome. This lets the existing Outbox processor mark a record processed only after publish success.

## Capabilities and topology

Every adapter declares `MessagingTransportCapabilities`. Unsupported features such as scheduling, partitioning, ordering, defer, dead-letter, transactions, or native batch publishing fail explicitly rather than being silently ignored.

`IMessageTopologyNamingStrategy` provides deterministic transport-neutral destination and subscription names. The default strategy validates bounded names and does not inject an environment name unless an explicit prefix is configured.

## Headers and sensitive data

Header propagation is allowlist-based and case-insensitive. Authentication cookies, credentials, API keys, access/refresh tokens, passwords, connection strings, and other forbidden headers are removed or rejected by policy. Payloads and sensitive header values must not be emitted to logs, traces, metrics, health responses, or generated verification artifacts.

W3C `traceparent` and `tracestate` are propagated only when syntactically valid. Invalid trace context is ignored safely.

## In-memory transport

`AddTcjInMemoryMessaging()` registers a bounded deterministic adapter for unit, conformance, and local-development scenarios. It is **non-durable** and is not a production broker. Process termination loses queued messages and adapter state.

## Native AOT and trimming

`TCJ.Messaging` declares `IsAotCompatible=true`. The default JSON serializer consumes explicit `JsonTypeInfo` metadata and does not activate arbitrary CLR types from wire data. The package is currently classified as **Conditional** in TCJ's Native AOT policy until a dedicated packed-NuGet Native AOT consumer is promoted to Full support evidence. Future adapters must independently preserve trimming/AOT safety.

See [Native AOT and trimming compatibility](../guides/native-aot-and-trimming.md).

## Adapter authoring

Future broker packages should implement only the transport-facing contracts, accurately declare capabilities, preserve envelope identity/metadata, map broker failures and settlements explicitly, and pass the reusable adapter conformance suite before release.

See [Messaging adapter authoring](../messaging-adapter-authoring.md) and [Transport-neutral messaging](../messaging.md).

Related package: [TCJ.Core](tcj-core.md). See also [transactional Outbox](../outbox.md), [transactional Inbox](../inbox.md), [health checks](../health-checks.md), [observability](../observability.md), and the [generated API reference](../api/index.md).
