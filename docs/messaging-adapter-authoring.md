# Messaging adapter authoring

A production adapter package implements the `TCJ.Messaging` transport contracts without changing the neutral package. Broker SDK dependencies belong in the adapter package only.

## Required registration surface

A selected adapter registers exactly one:

- `IMessagingTransportPublisher`;
- `MessagingTransportDescriptor`;
- `IMessagingTransportHealthProbe`.

When receive is supported, register one `IMessageReceiver`. When native batch publishing is declared, also register `IMessagingTransportBatchPublisher`.

Do not register multiple active descriptors/publishers and rely on DI ordering. `MessagingStartupValidator` intentionally rejects ambiguous transport selection.

## Capability declaration

`MessagingTransportCapabilities` is a contract, not documentation-only metadata. Declare only behavior the adapter actually supports:

- batch publish;
- scheduling;
- TTL;
- dead-letter;
- defer;
- ordered delivery model;
- partitioning;
- transactions;
- peek-lock/settlement;
- maximum payload, header, and batch limits.

If a capability is not supported, the adapter must fail explicitly with the neutral unsupported-capability contract. Never silently ignore scheduling, ordering, TTL, partition, or settlement requests.

## Envelope and serialization boundary

Adapters receive a validated `TransportMessageEnvelope`. Preserve `MessageId`, `MessageType`, `MessageVersion`, `ContentType`, correlation/causation, and permitted headers unchanged unless the broker requires a documented reversible mapping.

The adapter must not deserialize arbitrary CLR types. Application serialization belongs to `IMessageSerializer` and explicit `JsonTypeInfo`/contract registration. If an adapter adds protobuf, Avro, MessagePack, or another format, keep content-type handling explicit and deterministic.

## Header mapping and security

Run all inbound and outbound headers through TCJ's allowlist policy. Do not reintroduce removed headers at the adapter layer. Credentials, cookies, tokens, passwords, connection strings, broker connection metadata, and other secrets must not be copied into application headers, logs, traces, metrics, dead-letter descriptions, or verification artifacts.

Preserve valid W3C `traceparent` and `tracestate`. Invalid trace context must be ignored safely and must not prevent normal message policy from running.

## Exception and failure mapping

Translate broker failures to stable `PublishResult` outcomes and `MessagingFailureCategory` values. Retry ownership must be explicit:

- temporary connection/throttle/timeout -> transient/retryable;
- invalid topology, authentication/authorization, serialization/contract errors -> permanent unless the broker documents a safe transient condition;
- caller cancellation -> canceled, not transient failure;
- framework publish timeout -> timed out/retryable.

Do not expose raw broker exceptions as the only public result contract. Diagnostic exception detail may stay internal when safe.

## Receiving and settlement mapping

A `ReceivedMessage` contains a transport envelope, safe `DeliveryContext`, and `IMessageSettlement`. Delivery metadata may include partition/offset/sequence information, but neutral core logic must not depend on broker-specific fields.

Implement settlement operations according to declared capabilities. Double settlement must fail deterministically. If the broker does not support dead-letter/defer/retry semantics directly, either provide a documented equivalent or declare the capability unsupported.

The transactional Inbox bridge owns application processing. Transport acknowledgement must occur only after the Inbox result represents a committed application outcome.

## Cancellation, timeout, backpressure, and shutdown

All adapter async APIs must honor `CancellationToken`. Do not use unbounded task creation or unbounded channels. Broker prefetch/buffer settings must map to bounded limits and cooperate with `MaximumConcurrentMessages`.

Publish operations run under the framework's bounded timeout. Adapter code should stop its broker operation promptly when the token is canceled.

During graceful shutdown, stop accepting new deliveries first. Allow active work to finish within the configured grace period, then cancel remaining work. Do not simulate correctness with arbitrary sleeps or blind retries.

## Health and telemetry

Implement `IMessagingTransportHealthProbe` using bounded, non-sensitive readiness information. Health checks must not expose credentials, broker URLs containing secrets, payloads, or exception stack traces.

Use TCJ messaging activity/metric names where the neutral layer already emits them. Adapter-specific telemetry may be added, but metric labels must remain bounded. Never use message IDs, arbitrary destinations, arbitrary exception text, or payload/header values as high-cardinality dimensions.

## Conformance suite

Every adapter must run the reusable project:

```text
tests/TCJ.Messaging.ConformanceTests/
```

The adapter supplies a deterministic `MessagingAdapterHarness` implementation and executes the inherited `MessagingAdapterConformanceTests`. Required areas include:

- stable message ID and metadata preservation;
- allowed-header preservation and forbidden-header removal;
- content-type and correlation propagation;
- cancellation and timeout;
- transient/permanent failure classification;
- retry and duplicate delivery;
- dead-letter behavior;
- graceful shutdown/backpressure behavior;
- capability declaration and unsupported-capability behavior;
- telemetry;
- health checks.

Future adapter packages must pass the conformance suite before release. Production broker adapters should add their own integration tests in addition to the neutral conformance suite.

## Package and release checklist

Before publishing an adapter:

1. keep the broker SDK out of `TCJ.Core`, `TCJ.EntityFrameworkCore`, and `TCJ.Messaging`;
2. declare exact capabilities and bounded limits;
3. document topology, partitioning, ordering, retry ownership, and settlement mapping;
4. pass adapter conformance and broker integration tests;
5. validate cancellation, timeout, backpressure, and graceful shutdown under failure;
6. validate health checks and telemetry for secret/high-cardinality leakage;
7. run package consumer and upgrade compatibility tests;
8. run trimming/Native AOT validation when the adapter claims compatibility;
9. include package metadata, SBOM/provenance, and release evidence in the normal TCJ release gates.
