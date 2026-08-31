## Summary

Describe the problem and the approach taken.

## Contributor agreement

- [ ] I have read and agree to the [TCJ Contributor License Agreement](https://github.com/Amir-ESH/TCJ.Framework/blob/develop/CLA.md).
- [ ] I confirm that I have the right and authority to submit this contribution under the CLA, including any required employer/organization authorization.
- [ ] I understand that contributing does not grant ownership, merge/release authority, governance control, or trademark rights in the Official TCJ Project.

## Changes

-

## Compatibility

- [ ] No public API change
- [ ] Public API added or changed
- [ ] Breaking change documented
- [ ] Package validation passes against the published API baseline
- [ ] Any `CPxxxx` suppression is minimal and explained

## Validation

- [ ] Dependency security policy passes and no package source was added without review
- [ ] `dotnet restore TCJ.slnx --force-evaluate` reports no blocking advisory
- [ ] `dotnet build TCJ.slnx -c Release`
- [ ] `dotnet test TCJ.slnx -c Release --filter "Category!=SqlServer&Category!=AspNetCore&Category!=Concurrency"`
- [ ] Code coverage quality gate passes; new behavior has focused tests
- [ ] Mutation testing ran when applicable; the recorded baseline is valid and relevant survived mutants were reviewed
- [ ] Performance benchmarks pass for performance-sensitive changes
- [ ] No unexplained allocation regression is introduced
- [ ] Benchmark exclusions are documented
- [ ] Performance-policy changes include justification
- [ ] Generated benchmark output is not committed
- [ ] Architecture tests pass and module boundaries remain valid
- [ ] No new circular dependency is introduced
- [ ] New project references and dependency-direction changes are documented
- [ ] Infrastructure types do not leak into lower-level public APIs
- [ ] `architecture-policy` changes include justification
- [ ] `aot-policy` changes are explicit and justified as compatibility changes
- [ ] `dotnet pack TCJ.slnx -c Release --no-build` when packaging or release infrastructure changes
- [ ] Deterministic-build configuration remains enabled
- [ ] Independent package builds are reproducible
- [ ] Assemblies match between independent builds
- [ ] Portable PDBs match between independent builds
- [ ] Source Link metadata matches
- [ ] NuGet package metadata matches
- [ ] New reproducibility normalization rules are narrow, documented, tested, and justified
- [ ] Generated reproducibility artifacts are not committed
- [ ] The official release uses the verified package set
- [ ] Release-integrity automation remains valid and package checksums pass
- [ ] SBOM generation and verification succeed
- [ ] All six release packages are represented and symbol packages are present for all five runtime packages
- [ ] Package versions and dependency relationships match generated package metadata
- [ ] Required package and dependency hashes are present
- [ ] License metadata has been reviewed
- [ ] `sbom-policy` changes include justification
- [ ] Generated SBOM output is not committed
- [ ] `SHA256SUMS` includes the versioned SBOM
- [ ] New public APIs include XML documentation
- [ ] Public API parameters, type parameters, and return values are documented
- [ ] Required package and getting-started examples compile
- [ ] Conceptual and package documentation is updated
- [ ] Internal documentation links are valid
- [ ] DocFX metadata and site builds succeed
- [ ] Public API documentation coverage does not regress
- [ ] Documentation baseline changes are narrow and justified
- [ ] Generated documentation output is not committed
- [ ] Documentation updated where required
- [ ] No secrets or generated artifacts included
- [ ] SQL Server integration tests pass
- [ ] The SQL Server container image remains pinned
- [ ] No permanent database secret is required
- [ ] Test databases are isolated
- [ ] Migrations apply successfully
- [ ] Transaction behavior is verified
- [ ] Container logs are sanitized
- [ ] Generated SQL Server integration output is not committed
- [ ] Production changes discovered by integration tests are explained

- [ ] ASP.NET Core integration tests pass
- [ ] Application startup succeeds
- [ ] Exception mapping is verified
- [ ] Production responses hide sensitive details
- [ ] Current-user behavior is verified
- [ ] Request-scope isolation is verified
- [ ] Linux and Windows ASP.NET Core integration results are green
- [ ] Test authentication remains test-only
- [ ] ASP.NET Core diagnostics are sanitized
- [ ] Generated ASP.NET Core integration output is not committed

- [ ] All package consumers restore from the expected package source
- [ ] No compatibility consumer uses repository project references
- [ ] Resolved TCJ package versions match the candidate version
- [ ] TCJ package source identity is verified
- [ ] Linux, Windows, and macOS package consumers pass
- [ ] The full five-package consumer combination passes
- [ ] No package downgrade or dependency-conflict warning occurs
- [ ] Source and symbol package compatibility validation passes
- [ ] XML documentation, portable PDB, and Source Link validation passes
- [ ] Generated package-consumer compatibility output is not committed
- [ ] Compatibility-policy changes are explicit and justified

- [ ] Baseline upgrade consumers restore from NuGet.org
- [ ] Target upgrade consumers restore from the release-candidate package feed
- [ ] Baseline and target TCJ package versions and sources are verified
- [ ] Direct package upgrades pass without source changes
- [ ] Dependency graph changes are reviewed and no downgrade occurs
- [ ] Normalized runtime behavior remains compatible
- [ ] Breaking changes are declared and migration guidance is complete
- [ ] Required migration patches are explicit and pass guided validation
- [ ] Generated package-upgrade compatibility output is not committed
- [ ] Upgrade-compatibility policy changes are explicit and justified

- [ ] Property tests pass with required categories, iterations, deterministic seeds, and shrinking
- [ ] Fuzz targets complete without crashes, hangs, unexpected exceptions, or invariant violations
- [ ] Failure seeds and minimized inputs are reproducible
- [ ] Confirmed property or fuzz findings have conventional regression tests
- [ ] Fuzz input-size, per-input timeout, total-duration, and collection limits remain enforced
- [ ] Seed corpora are bounded, reviewed, and contain no sensitive values
- [ ] Generated fuzz artifacts and crash corpora are not committed
- [ ] Fuzzing-policy changes are explicit and justified

- [ ] Concurrency stress tests pass
- [ ] Deterministic stress seeds are replayable
- [ ] Dependency registration remains deterministic under parallel calls
- [ ] Request scopes and current-user identities remain isolated
- [ ] Domain events are neither duplicated nor lost
- [ ] EF Core and SQL Server concurrency boundaries are respected
- [ ] Deadlocks, hangs, timeouts, and invariant failures produce actionable traces
- [ ] Thread-safety contracts are documented
- [ ] Generated concurrency traces are not committed
- [ ] Concurrency-policy changes are explicit and justified

## Observability

- [ ] ActivitySource names remain stable
- [ ] Meter names remain stable
- [ ] Activity and metric contract changes are intentional and documented
- [ ] No sensitive telemetry tags are emitted
- [ ] Metric dimensions remain bounded
- [ ] Trace parenting and standard Activity propagation are correct
- [ ] Telemetry-disabled behavior is verified
- [ ] Telemetry-disabled overhead is measured
- [ ] Exporters remain optional and consumer-controlled
- [ ] Observability tests pass
- [ ] Generated telemetry artifacts are not committed

## Resilience

- [ ] Resilience boundaries and retry safety are documented
- [ ] Permanent failures and caller cancellation are not retried
- [ ] Retry, timeout, and circuit-breaker limits remain bounded
- [ ] SQL Server transaction retries recreate context and transaction state
- [ ] Domain-event retries do not duplicate successful handlers or undocumented side effects
- [ ] Resilience telemetry dimensions remain bounded and sensitive-safe
- [ ] Resilience tests pass, including applicable fault-injection and concurrency scenarios
- [ ] Generated resilience traces and reports are not committed

## Health checks

- [ ] Liveness remains dependency-independent
- [ ] Readiness reflects required dependencies
- [ ] SQL Server connectivity is tested
- [ ] Migration-state behavior is tested
- [ ] Health-check timeouts are bounded
- [ ] Health-check cancellation is propagated
- [ ] Health responses contain no secrets
- [ ] Detailed diagnostics are protected
- [ ] Concurrent health requests are safe
- [ ] Health-check contracts are updated intentionally
- [ ] Generated health-check artifacts are not committed

## Transactional outbox

- [ ] Business data and outbox records commit together
- [ ] Transaction rollback leaves no outbox row
- [ ] Stable outbox message IDs are preserved
- [ ] Duplicate outbox persistence is prevented
- [ ] Concurrent outbox claims are safe
- [ ] Outbox lease recovery is tested
- [ ] Transient outbox failures retry safely
- [ ] Poison outbox messages are isolated
- [ ] Outbox replay is explicit
- [ ] Outbox cleanup preserves pending records
- [ ] Outbox payloads remain out of logs and telemetry
- [ ] Outbox schema and contract changes are documented
- [ ] Generated outbox artifacts are not committed

## Related issue

Refs #
