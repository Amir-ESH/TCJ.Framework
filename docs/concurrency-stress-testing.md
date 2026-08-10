# Concurrency stress testing

Step 40 treats concurrency as an explicit compatibility contract. The suite tests only concurrency guarantees TCJ actually supports; it does not convert mutable framework or EF Core abstractions into thread-safe APIs merely to satisfy a stress test.

## Contract vocabulary

| Contract | Meaning | Representative TCJ usage |
|---|---|---|
| **Thread-safe** | The same instance may be used by multiple callers concurrently for the documented operation. | Immutable `Result` reads and `GuidGenerator.CreateVersion7` are exercised concurrently. |
| **Thread-compatible** | Independent instances can be used concurrently, but callers must synchronize shared mutable instances. | Registration options and application-owned mutable collections. |
| **Request-scoped only** | State belongs to one DI/request scope and must never be shared with another request. | `HttpContextCurrentUserProvider`, scoped repositories, scoped event handlers and `DbContext`. |
| **Single-operation only** | One instance is valid for a single logical operation at a time. Independent scoped instances may run concurrently. | EF Core `DbContext`, `IUnitOfWork`, and an active unit-of-work transaction. |
| **Not safe for concurrent use** | Concurrent mutation of the same instance is outside the supported contract. | Entity domain-event collections and arbitrary concurrent mutation of one `IServiceCollection` outside TCJ registration calls. |

`DbContext is not thread-safe`. TCJ therefore tests parallel work with independent scopes and contexts. The SQL Server scenario that deliberately overlaps commands on the same context verifies predictable rejection; it is not a promise that same-context concurrency is supported.

## Registration boundary

`AddTcjDependencyInjection` serializes mutations performed by concurrent invocations of the TCJ extension on the same `IServiceCollection`. This protects TCJ's `TryAdd`/`TryAddEnumerable` registration and scanning sequence and keeps the final descriptor set canonical. It does **not** make `IServiceCollection` generally thread-safe: application or third-party code concurrently mutating the same collection must use the same application-level synchronization boundary.

## Request and domain-event boundaries

`HttpContextCurrentUserProvider` is **Request-scoped only** through the current `HttpContext`. The ASP.NET Core stress host sends identities, roles, correlation values, cancellations, and failures concurrently and verifies that none leak across requests.

`DomainEventDispatcher` resolves handlers from its owning DI scope. Independent dispatch operations may run concurrently when each owns its scope. Per-operation dispatch is exactly-once for the supplied event list. TCJ does not promise global ordering between unrelated concurrent dispatches. The mutable domain-event collection on an entity is **Not safe for concurrent use**; aggregate mutation remains an application consistency boundary.

## EF Core and SQL Server

Repositories and Unit of Work are scoped around their `DbContext`. Supported concurrent usage is multiple independent scopes/contexts. One scope must never reuse another scope's context. Transaction rollback in one scope must not remove another scope's committed data, and unique constraints remain authoritative when inserts race.

The SQL Server tests reuse the pinned Testcontainers image from the repository SQL Server integration policy. They cover independent commits, rollback isolation, concurrent unique inserts, same-context rejection, and optimistic rowversion conflicts. Container diagnostics are sanitized before upload.

## Deterministic stress runner

`tests/TCJ.Concurrency.Tests/Infrastructure/StressRunner.cs` provides:

- configurable worker and iteration counts;
- deterministic scheduling seeds;
- a synchronized worker start;
- bounded `Task.Yield`/short-delay perturbation;
- per-operation and per-scenario timeouts;
- cancellation on timeout;
- duplicate and missing-operation detection;
- scope, identity, and transaction-interference counters;
- bounded operation timelines;
- replay metadata and JSON traces.

Policy defaults live in `eng/concurrency-policy.json`. Pull requests use a bounded `8 x 100` core workload; SQL scenarios use their separately bounded policy values. Scheduled runs use stronger worker/iteration values and three fixed seeds. A failed run is never retried to make the gate green.

## Local execution

Validate the contract first:

```bash
python3 eng/verify-concurrency.py validate-config
```

Run bounded core scenarios:

```bash
TCJ_STRESS_SEED=4001 dotnet test tests/TCJ.Concurrency.Tests/TCJ.Concurrency.Tests.csproj \
  -c Release \
  --filter "Category=Concurrency&Category!=AspNetCore&Category!=SqlServer"
```

ASP.NET Core request-isolation scenarios require no external service:

```bash
TCJ_STRESS_SEED=4001 dotnet test tests/TCJ.Concurrency.Tests/TCJ.Concurrency.Tests.csproj \
  -c Release \
  --filter "Category=Concurrency&Category=AspNetCore"
```

SQL Server scenarios require Docker because Testcontainers starts the pinned database image:

```bash
TCJ_STRESS_SEED=4001 dotnet test tests/TCJ.Concurrency.Tests/TCJ.Concurrency.Tests.csproj \
  -c Release \
  --filter "Category=Concurrency&Category=SqlServer"
```

## Replay a failed seed

Every trace records scenario, seed, workers, iterations, operating system, architecture, runtime, commit SHA, operation history, timeout state and replay command. Replay exactly one scenario with:

```bash
python3 tests/TCJ.Concurrency.Tests/scripts/replay-stress.py \
  --scenario ConcurrentRequestsKeepCurrentUsersIsolated \
  --seed 4001
```

Optional `--workers` and `--iterations` reproduce a reduced diagnostic configuration. The original trace remains authoritative even if a reduced replay is also useful.

## Results and failures

Generated output is written under:

```text
TestResults/Concurrency/
artifacts/concurrency/traces/
artifacts/concurrency/failures/
artifacts/concurrency/report/CONCURRENCY_SUMMARY.md
artifacts/concurrency/report/concurrency-summary.json
artifacts/concurrency/logs/
```

`eng/verify-concurrency.py verify` rejects failed/skipped critical scenarios, missing deterministic seeds or replay metadata, deadlocks, timeouts, duplicate/missing operations, scope or identity leakage, transaction interference, and unresolved failure traces. Failure traces are diagnostic artifacts, not source files, and must not be committed.

## CI and release behavior

The dedicated **Concurrency stress** workflow runs core, ASP.NET Core, and relevant SQL Server groups, publishes Markdown summaries, and uploads TRX/traces/logs. Pull-request work is bounded; the weekly run uses multiple deterministic seeds. Release preflight invokes the same reusable workflow against the release candidate. Official tag publication depends on successful concurrency jobs from the exact tagged commit and preserves commit-matched summaries/traces as release evidence. Any unresolved race, deadlock, hang, timeout, leakage, duplicate/missing operation or transaction-interference finding blocks the gate.


## Concurrent health readiness

Step 43 adds deterministic ASP.NET Core stress scenarios for readiness probes. Expensive checks use a per-check single-flight cache: concurrent requests may wait for the same bounded execution, cancellation must not corrupt later probes, and independent named checks never share cached state. The stress suite asserts that the underlying readiness execution never runs concurrently with itself and that a canceled request does not make later readiness requests fail or deadlock.
