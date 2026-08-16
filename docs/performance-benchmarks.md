# Performance benchmarks and regression gate

TCJ Framework uses BenchmarkDotNet to measure execution time and managed-memory allocations for representative `TCJ.Core` and `TCJ.DependencyInjection` operations. The goal is a repeatable performance signal, not a promise that an absolute nanosecond value from one machine will match another machine.

## Why BenchmarkDotNet

BenchmarkDotNet isolates benchmark execution, performs warmup and measurement iterations, reports statistical uncertainty, provides baseline ratios, and records managed allocations through `MemoryDiagnoser`. It also exports readable Markdown and machine-readable JSON, which lets the repository verifier enforce policy without scraping console output.

The benchmark project targets `net10.0`, references production projects directly, and contains no test-framework dependency. Benchmark-only fixture types live in the benchmark assembly; production APIs are not widened for measurement.

## Covered areas

The initial suite covers:

- successful and failed Result creation and successful-value access;
- GUID version 7 generation;
- guard, string, enumerable, and decimal extensions;
- public concrete-type discovery and dependency-marker classification;
- assembly scanning and transient, scoped, and singleton registration;
- repeated convention registration with duplicate protection enabled.

Future candidates include specification composition, repository query construction, EF Core model conventions, domain-event dispatching, ASP.NET Core middleware, and SQL Server integration benchmarks. External-infrastructure benchmarks are intentionally outside the the performance regression gate gate.

## Run locally

Restore and build first:

```bash
dotnet restore TCJ.slnx
dotnet build benchmarks/TCJ.Benchmarks/TCJ.Benchmarks.csproj \
  --configuration Release --no-restore
```

Run all benchmarks:

```bash
dotnet run \
  --project benchmarks/TCJ.Benchmarks/TCJ.Benchmarks.csproj \
  --configuration Release \
  --no-build \
  --no-restore \
  -- \
  --filter "*"
```

Use the short CI-style job when iterating locally:

```bash
TCJ_BENCHMARK_MODE=short dotnet run \
  --project benchmarks/TCJ.Benchmarks/TCJ.Benchmarks.csproj \
  --configuration Release \
  -- \
  --filter "*"
```

On PowerShell:

```powershell
$env:TCJ_BENCHMARK_MODE = "short"
dotnet run --project benchmarks/TCJ.Benchmarks/TCJ.Benchmarks.csproj `
  --configuration Release -- --filter "*"
```

Filter by class or method using BenchmarkDotNet's wildcard filter:

```bash
dotnet run --project benchmarks/TCJ.Benchmarks/TCJ.Benchmarks.csproj \
  -c Release -- --filter "*GuidVersion7Benchmarks*"

dotnet run --project benchmarks/TCJ.Benchmarks/TCJ.Benchmarks.csproj \
  -c Release -- --filter "*RegisterScopedDependency*"
```

Verify the reports after the run:

```bash
python3 eng/verify-performance-results.py verify
```

## Reports

BenchmarkDotNet writes its raw reports and the generated benchmark manifest under:

```text
artifacts/performance/reports/
```

The verifier writes:

```text
artifacts/performance/PERFORMANCE_SUMMARY.md
artifacts/performance/performance-summary.json
```

The dedicated workflow uploads JSON, Markdown, CSV, and log files and appends the Markdown summary to the GitHub Actions job summary. `BenchmarkDotNet.Artifacts/` and `artifacts/performance/` are generated paths and must not be committed.

## Baselines and comparison groups

Every benchmark class declares one BenchmarkDotNet baseline method. A baseline gives related methods in that class a readable reference point in BenchmarkDotNet output.

The blocking ratio gate is narrower: only methods listed in the generated `benchmark-manifest.json` with the same `comparisonGroup` are compared. These groups deliberately contain operations that should have equivalent work, such as a TCJ wrapper and its BCL equivalent or the three lifetime-registration paths. Operations such as Result creation and assembly discovery are still measured and reported, but they are not compared with an unrelated operation merely to manufacture a ratio.

`DecimalExtensionBenchmarks` is intentionally informational rather than ratio-blocking. The TCJ `RoundUp` API validates the requested decimal-place range and resolves a scale from its lookup table, while the illustrative BCL expression uses a scale prepared before measurement. Both values remain useful measurements, but treating their ratio as a regression gate would compare different amounts of work.

`StringExtensionBenchmarks` remains ratio-blocking, but its BCL baseline mirrors the public `EnsureEndsWith` contract: it performs the same null validation, uses runtime instance fields, and follows the same present/missing-suffix branches. This prevents a sub-nanosecond guard cost or compile-time constant folding from being mistaken for a framework regression.

Each comparison group must contain exactly one baseline. Missing baselines, missing methods, or duplicate results fail verification.

## Reading the statistics

- **Mean** is the average measured execution time in nanoseconds.
- **Error** is BenchmarkDotNet's standard error estimate for the mean.
- **StdDev** shows the spread of measured values; a large value signals noisy measurements.
- **Mean ratio** divides a comparison method's mean by its baseline mean from the same run. `1.00` is equal, `1.10` is about ten percent slower, and values below `1.00` are faster.
- **Allocated bytes** is managed memory allocated per operation.
- **Allocation ratio** divides allocated bytes by the baseline allocation in the same run.

When a baseline allocates zero bytes, small measured allocations are tolerated only up to `maximumUnexplainedAllocatedBytes`. Larger allocations fail rather than producing a misleading finite ratio.

## Why absolute hosted-runner times do not block

GitHub-hosted runners can differ in CPU model, neighboring workload, power behavior, virtualization, and operating-system image. Comparing today's raw nanoseconds with a previous run from an unrelated host can produce false regressions.

The the performance regression gate policy therefore blocks on ratios calculated inside a single BenchmarkDotNet run. The baseline and comparison method execute under the same runtime, operating system, process conditions, and machine load. Absolute values remain in artifacts for historical analysis, but they are not the only blocking condition.

## Repository policy

`eng/performance-policy.json` defines:

- the minimum number of completed benchmarks;
- required benchmark categories;
- the maximum relative mean ratio;
- the maximum relative allocation ratio;
- the allowance for allocations when the baseline allocates zero bytes.

Validate configuration without running benchmarks:

```bash
python3 eng/verify-performance-results.py validate-config
```

Validation rejects a missing or malformed policy, an ignored policy file, an incorrectly configured benchmark project, missing categories or baselines, fewer source benchmarks than required, missing workflow integration, or generated-output ignore mistakes.

Verification rejects missing or invalid BenchmarkDotNet reports, failed benchmarks, missing statistics or allocation data, non-finite values, incomplete categories, missing comparison baselines, and ratios above policy.

## Changing thresholds

A threshold change is a quality-policy change, not routine cleanup. The pull request must include:

1. the benchmark and comparison group affected;
2. before-and-after JSON or Markdown evidence;
3. an explanation of expected product impact;
4. why optimization or test-data correction is not appropriate;
5. the proposed new threshold and its scope.

Do not increase a global threshold to hide one noisy or incorrectly designed benchmark. Fix benchmark setup, choose a more meaningful comparison group, or document a narrowly accepted regression.

## Adding a benchmark category

1. Add realistic, deterministic benchmark data in setup methods.
2. Avoid network, disk, database, random generation, and wall-clock reads inside the measured method.
3. Add `[MemoryDiagnoser]`, `[BenchmarkCategory(...)]`, and one `[Benchmark(Baseline = true)]` method to the class.
4. Add every method to `BenchmarkCatalog` so the verifier knows the expected report set.
5. Add a `comparisonGroup` only when methods perform meaningfully equivalent work.
6. Add the category to `requiredBenchmarkCategories` only when every relevant run must contain it.
7. Run a short local job, then a full job, and inspect both time variability and allocations.

## Accepting a regression

A regression may be accepted when it buys a reviewed correctness, security, compatibility, or maintainability improvement and the cost is understood. The pull request must describe the tradeoff and include benchmark artifacts. If policy must change, keep the change explicit and justified; never silently remove the method, baseline, category, memory diagnostics, or report exporter.


## Health-check benchmarks

`HealthCheckBenchmarks` measures the lightweight core liveness path, cached readiness, uncached readiness with a fast in-process dependency, sanitized public-response serialization, and health telemetry disabled/enabled. The `HealthChecks` category is required by `eng/performance-policy.json`. Database network latency is intentionally excluded from the shared regression ratio; SQL Server behavior is validated by integration tests while the benchmarks catch framework overhead, allocation, and cache regressions.

## Transactional-outbox benchmarks

the transactional-outbox feature set adds an `Outbox` benchmark category for the provider-independent persistence path. `OutboxBenchmarks` records:

- `SaveChangesWithoutOutbox` as the informational class baseline;
- `SaveChangesWithOneEvent`;
- `SaveChangesWithFiveEvents`;
- `SerializeOneEvent`;
- `DeserializeOneEvent`.

The SaveChanges measurements use a fresh EF Core InMemory database root per operation so database growth does not contaminate later measurements. These methods are intentionally not placed in a blocking comparison group: persisting and serializing durable outbox messages performs additional correctness work, so treating it as equivalent to a SaveChanges call with no outbox would create a misleading regression ratio. SQL Server claim throughput remains covered by the dedicated outbox integration/concurrency evidence rather than a hosted-runner microbenchmark with external database latency.
