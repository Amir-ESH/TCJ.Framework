# TCJ.Messaging.Sagas.EntityFrameworkCore.SqlServer

`TCJ.Messaging.Sagas.EntityFrameworkCore.SqlServer` contains SQL Server-specific correctness behavior for TCJ durable Sagas. It is an opt-in leaf package and does not expose broker SDK types.

## Install

```bash
dotnet add package TCJ.Messaging.Sagas.EntityFrameworkCore.SqlServer --prerelease
```

Register the same application `DbContext` used by Inbox and Outbox:

```csharp
services.AddTcjSqlServerSagas<AppDbContext>();
```

and configure the model:

```csharp
modelBuilder.AddTcjSqlServerSagas();
```

The provider uses SQL Server `rowversion` for optimistic concurrency, a filtered unique index for active correlations, and atomic `UPDLOCK`/`READPAST`/`ROWLOCK` lease-based timer claiming. Process-local locks and global Saga serialization are not correctness mechanisms.

Consumers own and apply the resulting EF Core migration. SQL Server Saga timers are durable Saga-owned deadlines, not a general-purpose scheduler.

See [Durable Saga orchestration](../messaging-sagas.md), [TCJ.Messaging.Sagas](tcj-messaging-sagas.md), and [provider-neutral Saga EF integration](tcj-messaging-sagas-entityframeworkcore.md).
