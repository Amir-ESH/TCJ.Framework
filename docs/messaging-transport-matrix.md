# Messaging transport compatibility

The executable sources of this matrix are the runtime descriptors, the existing
messaging/adapter contracts, `eng/messaging-compatibility-contract.json`, and executed
tests. This document explains those expectations; it is not evidence that a run passed.

| Transport | Ordering scope | Retry | Dead-letter | Defer | Scheduling |
| --- | --- | --- | --- | --- | --- |
| InMemory | None | Emulated, non-durable | Emulated | Unsupported | Unsupported |
| RabbitMQ | BestEffort | ConventionBased TTL topology | ConventionBased topology | Unsupported | Unsupported |
| AzureServiceBus | PerSession | Emulated scheduled clone | Supported native subqueue | Supported native defer | Supported |
| Kafka | PerPartition | ConventionBased retry topic | ConventionBased DLT | Unsupported | Unsupported |

`Supported` means the declared public semantic has evidence. `Emulated` means TCJ
provides the behavior; `ConventionBased` requires documented routing/topology.
`TransportSpecific` identifies configuration that is not portable. `Unsupported`
requires explicit rejection; `NotApplicable` means no neutral operation exists (for
example a messaging transaction API). `Blocked` always fails aggregation.

RabbitMQ routing is transport-specific. BestEffort is tested with one publisher and
one receiver without recovery; this does not claim strict ordering under concurrency.
Kafka ordering evidence uses a single partition and separately requires the existing
contiguous-offset/revocation tests. Service Bus ordering requires its existing session
test, not an ordinary queue test. Native delivery tags, offsets, sequence numbers and
broker-generated metadata are excluded from logical envelope equality. JSON content is
compared structurally.

Inbox success precedes successful transport settlement. Database persistence, duplicate
business effects and outbound Outbox atomicity remain owned by the existing Inbox/Outbox
suites. The matrix composes their neutral bridges and preserves the independent feature
gates. A successful test double is not proof of a database commit. Outbox remains the
durable publication retry owner; broker-client retries are bounded operation retries.
Kafka DLT publication must precede source offset progression and cannot skip an unresolved
earlier offset. InMemory is bounded and non-durable and must not be used as proof of
broker durability.

Run locally on a Docker-enabled host:

```bash
export TCJ_MESSAGING_COMPATIBILITY_RESULTS="$PWD/TestResults/MessagingCompatibility/InMemory"
python eng/verify-messaging-compatibility.py validate-config
python eng/verify-messaging-compatibility.py record-context --results TestResults/MessagingCompatibility/InMemory
dotnet test tests/TCJ.Messaging.CompatibilityTests -c Release --filter "Transport=InMemory" \
  --logger "trx;LogFileName=matrix.trx" --results-directory "$TCJ_MESSAGING_COMPATIBILITY_RESULTS"
```

Set `TCJ_MESSAGING_COMPATIBILITY_RESULTS` to the absolute per-transport result directory.
The dedicated workflow runs RabbitMQ and Kafka with the existing pinned Testcontainers
fixtures, and Service Bus with the repository pinned emulator. No live-cloud credential
is required. Missing infrastructure fails the selected transport; it is never represented
as a successful skipped scenario.

The workflow also executes independent adapter workflows, the authoritative neutral
conformance suite, and package-only descriptor consumers. Neutral packed-package
publish/receive is exercised in process; deep broker scenarios use commit-matched adapter
evidence. Package dependency graphs, exact versions and package-source identity are checked
from isolated NuGet caches. Adapters remain opt-in.

The telemetry scenario runs the public neutral `MessageConsumerRunner` with each real
transport receiver and settlement. It checks publish, receive, consumer execution and
settlement activities, measured success-path metrics, bounded metric dimensions and
secret exclusion. Adapter-specific consumer-runner telemetry remains owned by its
independent adapter suite. Outbox fault injection occurs at the transport publisher
boundary, then retry uses the real adapter; it does not simulate a broker acknowledgement.

Aggregate only results from one commit, source-content digest, workflow run and run attempt:

```bash
python eng/verify-messaging-compatibility.py verify \
  --results TestResults/MessagingCompatibility \
  --output artifacts/messaging-compatibility
```

The verifier requires each transport, named capability/settlement evidence, neutral
Inbox/Outbox evidence and packed-package descriptors. It scans all evidence files plus
the generated JSON and Markdown for test secrets and credential patterns. Reports contain
bounded scenario identifiers and statuses; raw broker exceptions and message payloads
are omitted. Missing, stale, skipped or failed tests cannot generate a passing matrix.

Generated files are `compatibility-matrix.json` and `MESSAGING_COMPATIBILITY_SUMMARY.md`
under the ignored artifact directory. Release and PR orchestration require this workflow.
Any future production adapter must pass its own policy and integration suite, reuse the
neutral conformance suite, provide a compatibility fixture, declare capability evidence,
join the generated matrix and update this document before release.
