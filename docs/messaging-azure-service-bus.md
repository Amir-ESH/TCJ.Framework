# Azure Service Bus messaging transport

`TCJ.Messaging.AzureServiceBus` is the Azure Service Bus transport adapter for the transport-neutral contracts in `TCJ.Messaging`. Azure SDK types remain isolated from neutral TCJ packages.

## Delivery model

The adapter uses **Peek-Lock** receiving and manual settlement. `AutoCompleteMessages` is `false` and enabling it is rejected. A successful TCJ Inbox transaction is settled only after the Inbox result is available; duplicate Inbox outcomes are completed, retry outcomes use the configured transport retry strategy, and permanent failures can be dead-lettered.

With TCJ Outbox, publication is **at least once**: an Outbox record is only eligible to be marked processed after the Azure SDK send/schedule operation succeeds. Consumer delivery is also at least once, with TCJ Inbox remaining authoritative for idempotency. Azure Service Bus duplicate detection is a bounded broker feature and does **not** replace TCJ Inbox. The adapter does not provide or claim a global exactly-once guarantee.

When sessions are enabled, TCJ configures one in-flight handler per session and therefore claims ordered processing **within one session only**. There is no ordering guarantee across sessions.

## Installation

```bash
dotnet add package TCJ.Messaging --prerelease
dotnet add package TCJ.Messaging.AzureServiceBus --prerelease
```

Pin the exact TCJ preview version for reproducible deployments.

## Authentication

### Managed identity / workload identity / service principal

Use a `TokenCredential` in production. `DefaultAzureCredential` can select managed identity, workload identity, or another configured credential without putting a Service Bus secret in application configuration.

```csharp
using Azure.Identity;
using TCJ.Messaging.AzureServiceBus.Extensions;
using TCJ.Messaging.Extensions;

services.AddTcjMessaging();
services.AddTcjAzureServiceBus(
    "example.servicebus.windows.net",
    new DefaultAzureCredential(),
    options =>
    {
        options.PrefetchCount = 32;
        options.MaximumConcurrentMessages = 8;
        options.MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5);
    });
```

The application identity needs only send/receive permissions when topology is externally managed. `Declare` and `ValidateOnly` can require management permissions and should be granted separately and deliberately.

### Connection string

Connection strings are supported for environments where token credentials are not available:

```csharp
services.AddTcjMessaging();
services.AddTcjAzureServiceBus(
    configuration.GetConnectionString("ServiceBus")
        ?? throw new InvalidOperationException("Service Bus connection string is missing."));
```

Do not commit connection strings. Load production credentials from an environment-specific secret provider. Connection strings, credentials, payloads, raw application properties, and authorization material must not be written to logs, health responses, telemetry, or workflow artifacts.

## Client and sender lifecycle

The adapter creates one logical `ServiceBusClient` lazily for the registered transport and reuses it thread-safely. Senders are cached by configured destination with a bounded cache (`MaximumSenderCacheSize`, default `128`) and are disposed with the client. A sender is not created for every message.

Azure SDK retry is deliberately short and bounded (`MaximumRetries`, `RetryDelay`, `MaximumRetryDelay`, `TryTimeout`). Durable publish retry belongs to TCJ Outbox so SDK retry and durable retry do not form an unbounded stacked policy.

## Queue, topic, and subscription topology

Topology ownership is explicit:

- `Disabled`: infrastructure is owned outside the application. No management permission is required solely for registration.
- `ValidateOnly`: expected entities/properties are checked and mismatches fail as permanent topology errors.
- `Declare`: declared queues, topics, and subscriptions are created idempotently when supported by the management API.

Production environments should normally use infrastructure-as-code plus `Disabled` or `ValidateOnly`.

```csharp
using TCJ.Messaging.AzureServiceBus.Topology;

services.AddTcjAzureServiceBus(connectionString, options =>
{
    options.TopologyMode = AzureServiceBusTopologyMode.ValidateOnly;
    options.Topology.Queues.Add(new AzureServiceBusQueueOptions
    {
        Name = "orders",
        RequiresSession = false,
        RequiresDuplicateDetection = true,
        DuplicateDetectionHistoryTimeWindow = TimeSpan.FromMinutes(10)
    });
});
```

Conflicts such as a session requirement mismatch, duplicate-detection mismatch, missing queue/topic/subscription, or incompatible entity type are classified as permanent topology failures. Entity-name changes and session requirement changes are infrastructure migrations and must be reviewed before deployment.

## Message mapping

TCJ maps the stable logical `MessageId` to `ServiceBusMessage.MessageId` for normal publication. Logical message type maps to `Subject`; CLR full or assembly-qualified type names are not used. Schema version remains explicit in `tcj-message-version`.

The adapter preserves the neutral metadata required by TCJ, including:

- message ID, logical type and version;
- content type;
- correlation and causation IDs;
- `traceparent` and `tracestate` when valid;
- `OrderingKey` as Service Bus `SessionId` when session delivery is requested;
- partition key and reply-to metadata when supplied;
- allowlisted application properties only.

Unsupported/forbidden headers are rejected or removed by the TCJ header policy. Do not put tokens, connection strings, passwords, or unbounded user-controlled values into message headers.

## Scheduled delivery and TTL

`PublishContext.ScheduledAtUtc` uses the Service Bus broker scheduling API. TCJ does not implement scheduling with an in-memory timer. The timestamp must be in the future.

`PublishContext.TimeToLive` maps to message TTL, must be positive, and can be bounded further with `MaximumMessageTimeToLive`. Per-message TTL is independent from Outbox retention.

## Batch publishing and oversized messages

The adapter uses `ServiceBusMessageBatch` and `TryAddMessage`; it does not assume a fixed message count fits into a broker batch. Input/result order and stable logical IDs are preserved. An individual message that cannot fit is classified as a permanent payload failure and the payload body is not logged.

## Settlement

TCJ settlement maps to Service Bus operations as follows:

| TCJ outcome | Azure Service Bus operation |
| --- | --- |
| Complete | `CompleteMessageAsync` |
| Abandon | `AbandonMessageAsync` |
| DeadLetter | `DeadLetterMessageAsync` |
| Defer | `DeferMessageAsync` |
| Retry | configured strategy; default is scheduled clone then complete original |

Dead-letter reason/description values are bounded and sanitized. Defer is explicit: deferred messages leave normal delivery and must later be retrieved by sequence number. It is not a generic delayed-retry mechanism.

### Scheduled retry identity

With the default scheduled-clone retry strategy, the stable TCJ logical message ID remains in TCJ metadata while the transport `MessageId` receives a retry-attempt suffix. This avoids a broker duplicate-detection window suppressing the retry clone while preserving Inbox idempotency on the original logical ID. The original message is completed only after scheduling the retry succeeds.

## Lock renewal and lock loss

Message/session lock renewal is bounded by `MaxAutoLockRenewalDuration` (default five minutes, policy maximum thirty minutes). Renewal stops after handler completion or shutdown. A lost/expired lock is surfaced as a settlement failure and telemetry event; TCJ does not report a false completion. The broker can redeliver, and the Inbox must prevent duplicate committed business effects.

Long-running handlers must fit within the configured renewal policy. Avoid treating lock renewal as an unlimited execution lease.

## Sessions

Session-aware entities preserve session ID and use a session receiver. Defaults are:

```text
MaximumConcurrentSessions = 4
MaximumConcurrentCallsPerSession = 1
```

`MaximumConcurrentCallsPerSession` is intentionally required to remain `1` so the adapter's `PerSession` ordering capability is accurate. Multiple sessions can be processed concurrently within `MaximumConcurrentSessions`.

Session state is not a general TCJ persistence mechanism. Ordering across sessions, global ordering, and replay ordering are not guaranteed.

## Partitioning

`AzureServiceBusQueueOptions.EnablePartitioning` and `AzureServiceBusTopicOptions.EnablePartitioning` model broker-side partitioning explicitly. In `Declare` mode the adapter applies the setting when creating an entity; in `ValidateOnly` mode a mismatch is a permanent topology conflict. When both a TCJ `OrderingKey` (`SessionId`) and `PartitionKey` are supplied for one message, they must be identical as required by Azure Service Bus.

The official local Azure Service Bus emulator does not support partitioned entities. Keep partitioning disabled for emulator tests and validate partitioned topology against a protected Azure namespace before production deployment.

## Duplicate detection

Service Bus duplicate detection is configured on the queue/topic entity and applies only for the broker-configured history window. TCJ preserves stable logical IDs so duplicate detection can be useful for normal sends. It is still not indefinite deduplication and does not replace TCJ Inbox.

When duplicate detection is enabled and delayed retry uses a clone, distinguish the TCJ logical ID from the retry transport ID as described above.

## Outbox and Inbox integration

Register the standard TCJ messaging bridges exactly as with any other transport. The adapter is selected only through explicit `AddTcjAzureServiceBus` registration.

Outbox invariants:

- logical ID, type/version, correlation, causation, and trace context are preserved;
- successful send/schedule is required before the durable Outbox processor may mark the record processed;
- transient Service Bus failures remain retryable by Outbox;
- permanent authentication/topology/payload failures are not hidden by an infinite SDK retry loop.

Inbox invariants:

- production receive mode is Peek-Lock;
- no completion occurs before the Inbox pipeline returns a committed acknowledgement/duplicate outcome;
- duplicate broker delivery remains safe through Inbox idempotency;
- retry/dead-letter/defer/cancellation map to explicit settlement behavior;
- delivery count is diagnostic metadata, not an idempotency key.

## Observability

The adapter emits bounded activities and metrics under the `tcj.azure_service_bus.*` names recorded in `eng/azure-service-bus-contract.json`. Telemetry intentionally excludes message bodies, connection strings, credentials, raw application properties, message IDs by default, and session IDs by default.

Trace propagation uses W3C `traceparent`/`tracestate`. Malformed trace context is ignored safely rather than failing message processing.

## Health checks and startup diagnostics

`AddTcjAzureServiceBusHealthChecks` registers readiness-oriented checks for client, sender, processor, topology, and session processor state. Liveness must remain independent of Service Bus availability.

A readiness probe can open a sender link for the configured `ReadinessDestination` without sending a production message. Topology validation is skipped when topology mode is `Disabled`, so runtime send/receive does not require management access solely for health checks.

Startup validation checks authentication shape, bounds, topology consistency, manual completion, retry ownership, and declared transport capabilities without logging secrets.

## Graceful shutdown

Receiver/session loops observe cancellation, stop accepting new messages, stop lock renewal, and dispose receiver/client resources. A delivery whose Inbox work did not complete successfully is not falsely completed; broker redelivery remains possible.

## Local integration testing

Repository validation uses the official Azure Service Bus emulator for features it implements and a protected Azure namespace for cloud-only behavior. Run the static contract check first:

```bash
python3 eng/verify-azure-service-bus.py validate-config
```

Then run integration tests against an isolated emulator or test namespace:

```bash
dotnet test tests/TCJ.Messaging.AzureServiceBus.Tests/TCJ.Messaging.AzureServiceBus.Tests.csproj \
  --configuration Release \
  --logger "trx;LogFileName=azure-service-bus.trx" \
  --results-directory TestResults/AzureServiceBus

python3 eng/verify-azure-service-bus.py verify \
  --results TestResults/AzureServiceBus \
  --output artifacts/azure-service-bus
```

The dedicated GitHub Actions workflow sets `TCJ_AZURE_SERVICE_BUS_REQUIRE_INTEGRATION=1`; missing broker infrastructure therefore fails instead of silently skipping broker coverage.

The emulator is not evidence for every Azure-cloud behavior. Protected live validation is required for RBAC/managed identity, production topology permissions, and long-running lock/session behavior that the emulator does not faithfully reproduce.

## Production recommendations

- Prefer managed identity/workload identity via `TokenCredential` over connection strings.
- Use least-privilege send/receive roles; separate management permission when topology declaration/validation needs it.
- Keep SDK retry bounded and let TCJ Outbox own durable retry.
- Keep Inbox enabled for idempotency even when broker duplicate detection is enabled.
- Keep session concurrency bounded and preserve one in-flight call per session when ordering matters.
- Manage topology through infrastructure-as-code in controlled production environments.
- Alert on permanent authentication/topology failures, lock loss, processor errors, dead-letter growth, and readiness transitions.
- Never expose Service Bus secrets or message bodies through diagnostics.
