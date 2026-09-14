# TCJ.Messaging.Sagas.EntityFrameworkCore.SqlServer

`TCJ.Messaging.Sagas.EntityFrameworkCore.SqlServer` adds SQL Server-specific correctness behavior for TCJ durable Saga persistence. It layers on `TCJ.Messaging.Sagas.EntityFrameworkCore` and `TCJ.EntityFrameworkCore.SqlServer` without introducing broker or ASP.NET Core dependencies.

## Install

```bash
dotnet add package TCJ.Messaging.Sagas.EntityFrameworkCore.SqlServer --prerelease
```

## Highlights

- SQL Server `rowversion` optimistic concurrency for Saga instances and timers.
- Filtered database uniqueness for active Saga correlations.
- Atomic bounded due-timer claiming with `UPDLOCK`, `READPAST`, and `ROWLOCK`.
- Lease ownership and expiration so failed workers do not permanently strand timers.
- SQL uniqueness conflicts mapped to retryable EF concurrency semantics.
- No process-local lock or global Saga serialization correctness dependency.

## Registration

```csharp
services.AddTcjSqlServerInbox<AppDbContext>();
services.AddTcjSqlServerOutbox<AppDbContext>();
services.AddTcjSqlServerSagas<AppDbContext>();
```

Configure the application model:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);
    modelBuilder.AddTcjSqlServerSagas();
}
```

The same application `DbContext` must own Inbox, Outbox, business state, and Saga persistence for externally delivered Saga messages.

## Database migrations

Consumers own migrations. After adding the Saga model extension, generate and review an application migration before deployment. TCJ does not ship or automatically apply consumer database migrations.

## Timers

Saga timers are durable Saga-owned deadlines, not a general-purpose scheduler. SQL Server claiming uses bounded database leases. A timeout transition and timer completion are committed atomically by the timer processor, and an expired lease is recoverable after process failure.

## Native AOT and trimming

This package inherits the SQL Server/EF Core Native AOT experimental boundary and does not claim production Native AOT support. Provider-native Saga behavior remains usable in ordinary JIT deployments.

## Documentation

- [Durable Saga orchestration](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/messaging-sagas.md)
- [SQL Server integration](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/sqlserver.md)
- [Native AOT and trimming](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/guides/native-aot-and-trimming.md)
- [Repository](https://github.com/Amir-ESH/TCJ.Framework)

## License

TCJ Framework is licensed under GNU LGPL v3.0 only (`LGPL-3.0-only`). See the repository license for details.
