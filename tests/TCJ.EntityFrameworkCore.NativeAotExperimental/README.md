# TCJ EF Core NativeAOT experimental fixture

This project is **experimental project-reference evidence**, not a production support claim for either TCJ EF Core package.

It exists to verify the narrow static NativeAOT path documented by TCJ:

- publish as Native AOT for a concrete runtime identifier;
- use `Microsoft.EntityFrameworkCore.Tasks` during publish;
- generate the EF compiled model and precompile statically discoverable queries;
- opt in to `Microsoft.EntityFrameworkCore.GeneratedInterceptors`;
- configure the model explicitly rather than using TCJ runtime assembly discovery;
- register a generated Guid Strong ID explicitly through `StrongIdConversionRegistry` using its closed conversion expressions;
- compile that generated Strong ID in a small referenced fixture assembly so EF Core's secondary `MSBuildWorkspace` query-precompilation pass consumes the generated members from metadata instead of depending on source-generator execution inside that tooling workspace;
- exercise the SQL Server provider and TCJ SQL Server model conventions without requiring a live database.

The dedicated `TCJ.EntityFrameworkCore.NativeAotExperimental.StrongTypes` project is test-only. It consumes `TCJ.Generators` as an analyzer and produces the generated `ExperimentalRecordId` metadata before EF Core opens the startup project in its secondary Roslyn workspace. This keeps the test on the generated Strong ID path without adding a runtime generator dependency or weakening query precompilation.

The fixture intentionally does not use convention-based model scanning, `IEntitySearcher`, the transactional outbox runtime-discovery path, or TCJ soft-delete global query filters. The current EF compiled-model path does not support global query filters, so `ApplySoftDeleteQueryFilters()` is outside this NativeAOT experiment. Normal JIT consumers do not need the NativeAOT properties or EF publish tooling used here.

The representative query is intentionally rooted directly in the local startup `DbContext` and guarded by the `--execute-query` argument. EF Core 10's experimental query locator can precompile that static query during publish, while the default smoke execution remains database-independent. Do not move the query behind a helper method whose `DbContext` is a method parameter; that shape is currently classified as dynamic by EF's precompiler.

Passing this fixture does not promote `TCJ.EntityFrameworkCore` or `TCJ.EntityFrameworkCore.SqlServer` beyond the `Experimental` tier. A future support-tier upgrade requires packaged-consumer NativeAOT publish-and-execute evidence.
