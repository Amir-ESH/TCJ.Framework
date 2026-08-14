# Property-based and fuzz testing

TCJ Framework uses example-based tests, property-based tests, mutation testing, and fuzzing for different failure modes. Example tests document known cases. Property tests express invariants over generated inputs. Mutation testing checks whether the test suite detects deliberately changed production behavior. Fuzzing drives selected public entry points with malformed and boundary data while enforcing resource limits.

## Scope and tools

Step 39 initially targets `TCJ.Core` and `TCJ.DependencyInjection`, which are deterministic and require no database, Docker service, or network access. Property tests use the centrally pinned `FsCheck.Xunit.v3` package. The fuzz harness references the centrally pinned `SharpFuzz` package and also includes a deterministic managed campaign runner used by CI and release workflows; `--sharpfuzz` exposes the same targets through `Fuzzer.OutOfProcess.Run` for coverage-guided local campaigns.

## Properties, generators, shrinking, and replay

Properties live in `tests/TCJ.PropertyTests`. Every property has `MaxTest = 100`, a pinned `Replay` seed, the `Property` category, and a domain category. `PropertyArbitraries` supplies boundary-heavy Unicode, ASCII, whitespace, long-string, decimal, date/time, comparable, sequence, nullable-sequence, and cancellation inputs. Custom arbitraries include shrink functions that reduce strings and sequences toward small counterexamples.

FsCheck prints the original value, shrunk value, failing seed, and a direct replay seed when a property fails. Because each repository property also pins its initial `Replay` seed, rerunning only that property is deterministic:

```bash
dotnet test tests/TCJ.PropertyTests/TCJ.PropertyTests.csproj \
  --configuration Release \
  --filter "FullyQualifiedName~ResultProperties.SuccessPreservesValue"
```

For debugging a newly reported FsCheck failing-step seed, temporarily paste the reported tuple into that property's `Replay` value and run the filtered test. Do not commit a changed seed merely to hide a failure.

## Running property tests

```bash
python3 eng/verify-fuzzing.py validate-config

dotnet test tests/TCJ.PropertyTests/TCJ.PropertyTests.csproj \
  --configuration Release \
  --logger "trx;LogFileName=property-tests.trx" \
  --results-directory TestResults/PropertyTests

python3 eng/verify-fuzzing.py verify-properties \
  --results TestResults/PropertyTests \
  --output artifacts/fuzzing/property-report
```

The verifier checks the source-controlled property count, required categories, iteration floor, replay seeds, test outcomes, and summary contract.

## Fuzz targets and corpora

The required targets are `StringExtensions`, `Check`, `EnumerableExtensions`, `DependencyScanning`, and `ResultComposition`. Seed corpora live below `fuzz/corpus/`; these files are deliberately small, deterministic, reviewed, and non-sensitive. Add a seed only when it reaches a useful code path or represents an important boundary. Generated coverage or crash corpus material belongs in ignored artifact directories, never directly in the reviewed seed corpus.

Run one target:

```bash
dotnet build fuzz/TCJ.FuzzTests/TCJ.FuzzTests.csproj --configuration Release

dotnet run --no-build \
  --project fuzz/TCJ.FuzzTests/TCJ.FuzzTests.csproj \
  --configuration Release -- \
  --managed \
  --target StringExtensions \
  --duration 30 \
  --corpus fuzz/corpus/strings \
  --output artifacts/fuzzing/fuzz-results/StringExtensions \
  --seed 39039 \
  --max-input-bytes 1048576 \
  --timeout-ms 1000
```

Run all policy-required targets with the external watchdog:

```bash
python3 fuzz/scripts/run-fuzz.py \
  --duration 30 \
  --output artifacts/fuzzing/fuzz-results \
  --seed 39039

python3 eng/verify-fuzzing.py verify-fuzz \
  --results artifacts/fuzzing/fuzz-results \
  --output artifacts/fuzzing/fuzz-report \
  --minimum-duration-seconds 30
```

A SharpFuzz-compatible target can also be launched with `--sharpfuzz <target>` after instrumenting the assemblies according to SharpFuzz's tooling instructions.

## Resource limits and failure classification

Policy limits cap input size, corpus-entry size, collection size, process working set, per-input execution time, and total target duration. The C# campaign uses a per-case watchdog and the Python runner supplies a separate process-level watchdog so a hard hang or process death is still classified. Findings are classified as crash, hang, unexpected exception, invariant violation, resource exhaustion, expected validation failure, or tooling failure. Blocking categories never pass verification.

Failure inputs are named by SHA-256 rather than raw data, copied to a controlled failure directory, and minimized by the managed runner where possible. Metadata is size-limited and common credential markers are redacted or rejected. Corpus bytes are never interpolated into shell commands or executed as scripts.

## Regression rule

A confirmed property or fuzz finding is not complete when the fuzzer merely stops finding it. Preserve the minimized reproducer, add a conventional deterministic regression test, link the issue or pull request, fix the underlying behavior, and remove unresolved failure material only after the regression test and fuzz target pass. Broad exception swallowing is not an acceptable fix.

## CI and release use

Normal CI validates the policy and runs the bounded property suite. The dedicated `Property and fuzz testing` workflow runs property tests plus five 30-second fuzz targets on relevant pull requests and pushes, and runs 10-minute-per-target campaigns weekly or on manual dispatch. Release preflight and the tag-based release workflow rerun the property suite and all required fuzz targets on the exact release source. A failure blocks release readiness and NuGet publication. Markdown and JSON summaries, TRX files, logs, failure inputs, and minimized reproducers are uploaded as GitHub Actions artifacts.
