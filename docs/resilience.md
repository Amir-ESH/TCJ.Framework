# Resilience policies and fault-injection testing

TCJ exposes small, backend-neutral resilience primitives for operations that have an explicitly reviewed retry, timeout, or circuit-breaker boundary. Resilience is **not** applied globally and registering the services does not wrap arbitrary application code.

The design keeps six boundaries separate:

- **Operation-level retry** retries one explicitly safe logical operation.
- **Transaction-level retry** retries the complete SQL Server transaction delegate through the provider execution strategy.
- **Handler-level retry** is an opt-in policy for one failing domain-event handler.
- **Request-level timeout** belongs at the application/request boundary; TCJ's timeout primitive is cooperative and operation-scoped.
- **Database command timeout** remains an EF Core/provider concern configured through `TcjSqlServerOptions.CommandTimeout`.
- **Circuit-breaker protection** belongs to one bounded dependency/operation category and is never shared globally across unrelated endpoints or tenants.

These distinctions are important. Retrying a command inside a failed transaction, replaying an entire domain-event dispatch, or stacking an application retry loop around SQL Server's provider retry strategy can duplicate writes or multiply outage load.

## Register resilience primitives

`TCJ.DependencyInjection` provides optional registration:

```csharp
services.AddTcjResilience(options =>
{
    options.Retry.MaxRetryAttempts = 3;
    options.Retry.BaseDelay = TimeSpan.FromMilliseconds(200);
    options.Retry.MaxDelay = TimeSpan.FromSeconds(5);
    options.Retry.UseJitter = true;

    options.Timeout.OperationTimeout = TimeSpan.FromSeconds(30);

    options.CircuitBreaker.FailureThreshold = 5;
    options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
});
```

Registration is idempotent. It registers `TcjRetryPolicy`, `TcjTimeoutPolicy`, `ITransientFailureDetector`, `TimeProvider`, and transient `TcjCircuitBreaker` instances. A circuit is deliberately stateful, so applications that need a circuit shared by one specific downstream dependency should explicitly own that breaker in the appropriate service lifetime rather than using a process-wide keyed dictionary with unbounded keys.

The framework bounds configuration:

| Setting | Default | Framework maximum |
|---|---:|---:|
| Retry attempts after the initial call | 3 | 5 |
| Retry base delay | 200 ms | must not exceed max delay |
| Retry maximum delay | 5 s | 30 s |
| Operation timeout | 30 s | 120 s |
| Circuit failure threshold | 5 | 100 |
| Circuit break duration | 30 s | 5 min |

Zero retry attempts disable retries. Negative retry values, invalid delay relationships, non-positive timeout/break durations, and values above the framework bounds fail validation during registration or policy construction.

## Transient-failure classification

`TransientFailureDetector` is intentionally conservative. It recognizes:

- `TimeoutException` as potentially transient when an operation has explicitly chosen retry semantics;
- a `DbException` only when the provider reports `DbException.IsTransient == true`;
- additive consumer classifiers registered as `ITransientFailureClassifier`.

Caller cancellation (`OperationCanceledException`) is never transient by default. Argument/validation errors, authorization/authentication failures, permanent configuration failures, and deterministic coding defects are not retried by the built-in detector.
These hard safety exclusions are evaluated before consumer classifiers, so an additive classifier cannot accidentally turn an argument, cancellation, authorization/authentication, or deterministic configuration/coding failure into a retryable failure.

TCJ does not maintain its own undocumented SQL Server error-number table. SQL Server command/connection retry classification remains owned by EF Core's SQL Server execution strategy. Consumers may still use the existing `TcjSqlServerOptions.AdditionalTransientErrorNumbers` provider hook when they have a documented, tested provider-specific reason.

## Operation-level retry

Use `TcjRetryPolicy` only around an operation whose repeat semantics are understood:

```csharp
var result = await retryPolicy.ExecuteAsync(
    token => dependency.ReadAsync(token),
    strategy: "operation",
    cancellationToken);
```

The initial call plus at most `MaxRetryAttempts` are executed. Delays use bounded exponential equal jitter. Cancellation interrupts the delay immediately. A permanent exception is surfaced on the first failure, and retry exhaustion rethrows the last operation failure without replacing its cause.

The strategy argument is for stable operation categories, not endpoint names or identifiers. TCJ maps unknown consumer strategy labels to `custom` in telemetry so metric dimensions remain bounded.

### Side effects and idempotency

A generic retry policy cannot prove whether an application side effect is idempotent. Before retrying a write, external call, message publication, or scheduled operation, the consumer must provide an idempotency boundary such as a natural unique key, operation identifier, outbox/inbox record, or downstream idempotency token.

Step 42 deliberately does **not** add a distributed idempotency store. That storage and consistency choice belongs to the consuming application.

## Cooperative timeout

`TcjTimeoutPolicy` creates a timeout token linked to the caller token and awaits the operation to completion:

```csharp
await timeoutPolicy.ExecuteAsync(
    token => dependency.CallAsync(token),
    strategy: "operation_timeout",
    cancellationToken);
```

If the caller token is canceled, the original `OperationCanceledException` propagates. If the policy deadline fires first, TCJ throws `TcjTimeoutException` containing the configured timeout. The implementation does not abandon the delegate as unobserved background work.

The policy is cooperative: the operation must honor its cancellation token. It is not a mechanism for forcefully terminating arbitrary synchronous or non-cooperative code.

## Circuit breaker

`TcjCircuitBreaker` has deterministic `Closed`, `Open`, and `HalfOpen` states:

1. It starts `Closed`.
2. Consecutive **transient** failures reach `FailureThreshold` and open the circuit.
3. Calls while open fail fast without invoking the underlying operation.
4. After `BreakDuration`, the circuit becomes `HalfOpen` and admits one probe.
5. A successful probe closes the circuit; a transient probe failure reopens it.

Permanent operation errors are propagated but do not open the circuit. One half-open probe is admitted at a time. A caller-canceled probe does not get reclassified as an internal failure.

Do not share one breaker across unrelated services, tenants, or endpoints. The default DI registration is transient specifically to avoid accidental cross-dependency state coupling.

## SQL Server transaction-level retry

`AddTcjSqlServer` already configures SQL Server's provider-supported execution strategy. Step 42 keeps that database-level retry responsibility with EF Core and adds an explicit transaction-level helper for user-initiated transactions. When provider retries are enabled, TCJ validates `MaxRetryCount` in the range 1–10 and `MaxRetryDelay` above zero and no greater than 30 seconds; the existing defaults remain 6 retries and 30 seconds.

```csharp
await scopeFactory.ExecuteTcjSqlServerTransactionAsync<MyDbContext>(
    async (db, token) =>
    {
        db.Orders.Add(order);
        await db.SaveChangesAsync(token);
    },
    cancellationToken);
```

The helper asks the configured provider for `CreateExecutionStrategy()` and executes the **complete transaction delegate** through it. Every execution-strategy attempt receives a new DI scope, a new `DbContext`, and a new transaction. A failed context/transaction is never reused, and TCJ commits only after the delegate completes successfully.

Do not add a second `TcjRetryPolicy` around this helper. That would stack an operation retry loop on top of the provider strategy. Also remember the commit-unknown problem: if the connection drops while commit is being acknowledged, an application may need a natural/idempotent key or another application-level reconciliation strategy to prove that replay is safe.

## Domain-event resilience

Domain-event retries are **off by default**. This preserves the existing sequential dispatcher semantics and avoids silently duplicating non-idempotent handler side effects.

An application may explicitly enable retry of the **individual failing handler**:

```csharp
services.AddTcjDomainEventResilience(options =>
{
    options.RetryTransientHandlerFailures = true;
    options.Retry.MaxRetryAttempts = 2;
    options.Retry.BaseDelay = TimeSpan.FromMilliseconds(100);
    options.Retry.MaxDelay = TimeSpan.FromSeconds(1);
});
```

Only the handler that throws a classified transient exception is retried. Successful handlers earlier in the dispatch sequence are not replayed, later handlers are not invoked until the failing handler succeeds, permanent errors fail immediately, and caller cancellation stops retry delay and dispatch.

Enabling handler retry is appropriate only when the retried handler's side effects are idempotent or independently protected. TCJ does not infer idempotency from handler type or event type.

## Telemetry

Step 42 extends the Step 41 `TCJ.Core` `ActivitySource` and `Meter` contract with:

Activities:

- `tcj.resilience.execute`
- `tcj.resilience.retry`
- `tcj.resilience.timeout`
- `tcj.resilience.circuit_breaker`

Metrics:

- `tcj.resilience.attempts`
- `tcj.resilience.retries`
- `tcj.resilience.timeouts`
- `tcj.resilience.circuit_open`
- `tcj.resilience.failures`

Dimensions are limited to bounded strategy/outcome/attempt/failure-category/circuit-state values. Raw exception messages, SQL, connection strings, identifiers, and endpoint keys are not recorded. Exporters remain entirely consumer-controlled.

## Deterministic fault injection

`tests/TCJ.Resilience.Tests` contains reusable test-only helpers under `Infrastructure/`. Scenarios can fail the first N attempts or selected attempts, delay checkpoints, inject selected exception types, trigger cancellation, record attempt history, and verify committed side-effect counts.

Retry, timeout, and circuit recovery tests use `FakeTimeProvider` where practical rather than sleeping real seconds. SQL Server-specific scenarios use the repository's pinned Testcontainers SQL Server image and controlled EF execution strategies. Generated histories are written only when `TCJ_RESILIENCE_TRACE_DIR` is configured by CI.

Run the static contract validation:

```bash
python3 eng/verify-resilience.py validate-config
```

Run the fast deterministic scenarios:

```bash
dotnet test tests/TCJ.Resilience.Tests/TCJ.Resilience.Tests.csproj \
  -c Release \
  --filter "Category!=SqlServer" \
  --logger "trx;LogFileName=resilience-core.trx" \
  --results-directory TestResults/Resilience
```

The dedicated resilience workflow also executes SQL Server scenarios and then verifies real TRX/trace evidence:

```bash
python3 eng/verify-resilience.py verify \
  --results TestResults/Resilience \
  --traces artifacts/resilience/traces \
  --output artifacts/resilience/report
```

`TestResults/Resilience/` and `artifacts/resilience/` are generated evidence and must not be committed.

## Anti-patterns

Avoid these patterns:

- retrying every exception;
- treating caller cancellation as transient;
- nesting TCJ operation retries around EF Core's SQL Server execution strategy;
- retrying one SQL command inside a transaction that has already failed;
- reusing a failed `DbContext` for a transaction retry;
- retrying an entire domain-event dispatch after some handlers already succeeded;
- sharing one circuit across unrelated dependencies;
- using user IDs, URLs, tenant IDs, SQL, or exception messages as resilience telemetry dimensions;
- increasing retry count or timeout simply to make a failing fault-injection test pass.
