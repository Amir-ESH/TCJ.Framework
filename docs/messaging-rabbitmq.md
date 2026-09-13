# RabbitMQ transport

`TCJ.Messaging.RabbitMQ` is explicitly opt-in. Register `AddTcjMessaging()` followed by
`AddTcjRabbitMq(...)`. The adapter uses publisher confirms and mandatory publication;
an unroutable message cannot be reported as successfully published.

Delivery with Outbox is at-least-once. Inbox owns durable idempotency; neither broker
redelivery nor publisher confirm provides global exactly-once application effects.
Retry uses declared TTL queues and dead-letter routing, with bounded attempts and a
terminal dead-letter queue. Complete acknowledges only after the Inbox pipeline returns.

Topology ownership is explicit: Declare creates topology, ValidateOnly checks existing
topology, and Disabled uses externally managed topology. Credentials must come from
application configuration and must never be included in diagnostics.

Ordering is BestEffort, not global strict ordering. Routing uses the documented
message-type/version strategy (or an explicitly configured routing strategy).
Batch publish, scheduling, transactions and defer are unsupported. TTL and dead-letter
require the configured topology; RabbitMQ retry is convention-based.

The Testcontainers fixture pins `rabbitmq:4.3.5-alpine`, uses disposable credentials and
isolated destinations. Run `python eng/verify-rabbitmq.py validate-config`, then
`dotnet test tests/TCJ.Messaging.RabbitMQ.Tests -c Release` on a Docker-enabled host.
The dedicated workflow verifies TRX results before publishing its summary.

See [the transport matrix](messaging-transport-matrix.md) for cross-transport semantics.
