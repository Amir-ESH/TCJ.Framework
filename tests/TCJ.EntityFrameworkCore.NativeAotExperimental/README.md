# TCJ EF Core NativeAOT experimental fixture

This project is **experimental project-reference evidence**, not a production support claim for either TCJ EF Core package.

It exists to verify the narrow static NativeAOT path documented by TCJ:

- publish as Native AOT for a concrete runtime identifier;
- use `Microsoft.EntityFrameworkCore.Tasks` during publish;
- generate the EF compiled model and precompile statically discoverable queries;
- opt in to `Microsoft.EntityFrameworkCore.GeneratedInterceptors`;
- configure the model explicitly rather than using TCJ runtime assembly discovery;
- register a Guid Strong ID explicitly through `StrongIdConversionRegistry` using the same closed expression shape emitted by `TCJ.Generators`;
- keep the NativeAOT startup source independent of source-generator execution inside EF Core's secondary `MSBuildWorkspace` query-precompilation pass;
- exercise the SQL Server provider and TCJ SQL Server model conventions without requiring a live database.

The fixture intentionally mirrors the generated `ExperimentalRecordId` conversion surface in source instead of invoking `TCJ.Generators` inside this startup project. EF Core 10 query precompilation recompiles startup sources in a secondary Roslyn `MSBuildWorkspace`; on the SDK used by CI, analyzer-generated members are not reliably available in that secondary compilation even though the normal build succeeds. Generator output shape remains covered by `TCJ.Generators.Tests`, while the EF model and SQL Server integration suites exercise real generated Strong IDs. This fixture therefore isolates the TCJ-owned EF conversion/model path under NativeAOT without weakening EF compiled-model or query-precompilation coverage.

The fixture intentionally does not use convention-based model scanning, `IEntitySearcher`, the transactional outbox runtime-discovery path, or TCJ soft-delete global query filters. The current EF compiled-model path does not support global query filters, so `ApplySoftDeleteQueryFilters()` is outside this NativeAOT experiment. Normal JIT consumers do not need the NativeAOT properties or EF publish tooling used here.

The representative query is intentionally rooted directly in the local startup `DbContext` and guarded by the `--execute-query` argument. EF Core 10's experimental query locator can precompile that static query during publish, while the default smoke execution remains database-independent. Do not move the query behind a helper method whose `DbContext` is a method parameter; that shape is currently classified as dynamic by EF's precompiler.

Passing this fixture does not promote `TCJ.EntityFrameworkCore` or `TCJ.EntityFrameworkCore.SqlServer` beyond the `Experimental` tier. A future support-tier upgrade requires packaged-consumer NativeAOT publish-and-execute evidence.
