# TCJ.Messaging.Sagas

`TCJ.Messaging.Sagas` contains the transport-neutral contracts for durable Saga / Process Manager orchestration in TCJ Framework. It defines strongly typed Saga state, explicit lifecycle transitions, deterministic correlation, bounded retry policies, Saga-owned timer abstractions, compensation/remediation contracts, diagnostics, and explicit state migration contracts without an EF Core, SQL Server, ASP.NET Core, or broker SDK dependency.

## Install

```bash
dotnet add package TCJ.Messaging.Sagas --prerelease
```

TCJ Framework is currently pre-1.0. Pin the exact preview version used by your application when reproducibility matters.

## Highlights

- Strongly typed `ISagaState` application state.
- Stable logical Saga types and explicit definition/state versions.
- Explicit deterministic `Guid`, `string`, `long`, and `int` correlation keys.
- Case-sensitive string correlation with no implicit trimming or casing changes.
- Explicit start, continuation, timeout, completion, failure, and compensation contracts.
- Bounded retry, timer, compensation, state-payload, and cleanup options.
- Transport-neutral `ISagaTimerProcessor` and `ISagaRemediationService` abstractions.
- Activities and metrics with bounded, sanitized dimensions.
- No broker SDK, EF Core, SQL Server, or ASP.NET Core dependency.

## Persistence

This package does not persist Saga state. Use `TCJ.Messaging.Sagas.EntityFrameworkCore` plus a provider package such as `TCJ.Messaging.Sagas.EntityFrameworkCore.SqlServer` for durable persistence.

## Durability boundary

Externally delivered Saga messages are designed to use the existing transactional Inbox and Outbox. Direct broker publication from a Saga database transaction is not the recommended durability path.

## Native AOT and trimming

Definitions are registered explicitly and state serialization uses caller-provided `JsonTypeInfo<TState>`. Runtime assembly scanning and assembly-qualified persisted CLR type names are not supported. The package is recorded as Conditional in the repository AOT policy until packed-NuGet Native AOT execution evidence exists.

## Documentation

- [Durable Saga orchestration](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/messaging-sagas.md)
- [Transport-neutral messaging](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/messaging.md)
- [Transactional Inbox](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/inbox.md)
- [Transactional Outbox](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/outbox.md)
- [Native AOT and trimming](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/guides/native-aot-and-trimming.md)
- [Repository](https://github.com/Amir-ESH/TCJ.Framework)

## License

TCJ Framework is licensed under GNU LGPL v3.0 only (`LGPL-3.0-only`). See the repository license for details.
