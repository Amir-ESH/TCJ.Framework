# TCJ.Messaging.Sagas.EntityFrameworkCore

`TCJ.Messaging.Sagas.EntityFrameworkCore` supplies provider-neutral durable Saga persistence and integrates Saga handlers with TCJ's existing transactional Inbox and Outbox.

## Install

```bash
dotnet add package TCJ.Messaging.Sagas.EntityFrameworkCore --prerelease
```

External Saga messages do not create a second transaction. The existing Inbox owns the transaction and the Saga integration modifies the same scoped application `DbContext`, so Inbox state, Saga state, business changes, Saga timers, and Outbox records commit or roll back together. Startup validation rejects an Inbox/Outbox/Saga `TDbContext` mismatch.

Application state remains strongly typed and is serialized with explicitly registered `JsonTypeInfo<TState>`. Framework lifecycle metadata, concurrency metadata, and versions are persisted separately from the application payload. State payload size is bounded.

Consumers own migrations. Add the provider-specific Saga model configuration to `OnModelCreating`, generate and review the application migration, and apply it through the application's normal deployment process. TCJ does not auto-apply Saga migrations.

See [Durable Saga orchestration](../messaging-sagas.md) and [TCJ.Messaging.Sagas](tcj-messaging-sagas.md).
