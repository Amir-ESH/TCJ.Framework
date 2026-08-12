# TCJ EF Core NativeAOT experimental fixture

This project is **experimental project-reference evidence**, not a production support claim for either TCJ EF Core package.

It exists to verify the narrow static NativeAOT path documented by TCJ:

- publish as Native AOT for a concrete runtime identifier;
- use `Microsoft.EntityFrameworkCore.Tasks` during publish;
- generate the EF compiled model and precompile statically discoverable queries;
- opt in to `Microsoft.EntityFrameworkCore.GeneratedInterceptors`;
- configure the model explicitly rather than using TCJ runtime assembly discovery;
- exercise the SQL Server provider and TCJ SQL Server model conventions without requiring a live database.

The fixture intentionally does not use convention-based model scanning, `IEntitySearcher`, the transactional outbox runtime-discovery path, or TCJ soft-delete global query filters. The current EF compiled-model path does not support global query filters, so `ApplySoftDeleteQueryFilters()` is outside this NativeAOT experiment. Normal JIT consumers do not need the NativeAOT properties or EF publish tooling used here.

Passing this fixture does not promote `TCJ.EntityFrameworkCore` or `TCJ.EntityFrameworkCore.SqlServer` beyond the `Experimental` tier. A future support-tier upgrade requires packaged-consumer NativeAOT publish-and-execute evidence.
