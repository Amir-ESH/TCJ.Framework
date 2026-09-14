# TCJ.Messaging.Sagas.EntityFrameworkCore

`TCJ.Messaging.Sagas.EntityFrameworkCore` provides provider-neutral EF Core persistence and transaction integration for TCJ durable Sagas. It joins Saga processing to the existing transactional Inbox and Outbox on the same application `DbContext` instead of introducing a second receive transaction or a second Outbox.

## Install

```bash
dotnet add package TCJ.Messaging.Sagas.EntityFrameworkCore --prerelease
```

A provider-specific Saga package is also required for production persistence behavior. For SQL Server, install `TCJ.Messaging.Sagas.EntityFrameworkCore.SqlServer`.

## Highlights

- Framework-owned Saga instance, correlation, and timer persistence models.
- Application state serialized from explicitly supplied `JsonTypeInfo<TState>` metadata.
- Framework lifecycle metadata stored separately from strongly typed application state.
- Explicit Saga definition registration; no assembly-scanning requirement.
- Transactional Inbox adapters using the same scoped consumer `DbContext`.
- Existing Outbox-compatible domain-event emission for durable outbound work.
- State migration/upcasting, bounded cleanup, remediation, readiness, and liveness.
- Provider-neutral API with no SQL Server or broker SDK dependency.

## Registration

Register the provider-neutral infrastructure before individual Saga definitions. Production applications normally use a provider extension that calls this registration, such as `AddTcjSqlServerSagas<TDbContext>()`.

Every Saga definition supplies a stable logical Saga type, positive definition and state-schema versions, explicit `JsonTypeInfo<TState>`, an explicit state factory, message contracts, correlation rules, terminal states, and optional timeout/compensation/migration behavior.

## Database migrations

The application owns EF Core migrations. Include the provider's Saga model extension in `OnModelCreating`, generate an application migration, review it, and deploy it using the application's normal migration process. TCJ does not apply Saga migrations automatically.

## Transaction ownership

For external Saga messages, the existing transactional Inbox owns the database transaction. Saga infrastructure modifies the same `DbContext` and allows the Inbox pipeline to call `SaveChanges`. Saga state, business data, Saga timer mutations, Inbox state, and Outbox persistence therefore share the same local transaction.

## Native AOT and trimming

State serialization is explicit and avoids reflection-only serializer registration, but this package inherits the repository's EF Core Native AOT experimental boundary and does not claim production Native AOT support.

## Documentation

- [Durable Saga orchestration](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/messaging-sagas.md)
- [Architecture](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/architecture.md)
- [Transactional Inbox](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/inbox.md)
- [Transactional Outbox](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/outbox.md)
- [Repository](https://github.com/Amir-ESH/TCJ.Framework)

## License

TCJ Framework is licensed under GNU LGPL v3.0 only (`LGPL-3.0-only`). See the repository license for details.
