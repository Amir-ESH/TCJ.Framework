# TCJ Source Generator Development

`TCJ.Generators` uses Roslyn incremental generators. Keep pipelines deterministic, avoid mutable generator state, and prefer attribute providers over whole-compilation scans.

When adding generation scenarios:

- Add golden tests for generated output.
- Keep hint names stable.
- Report compiler diagnostics instead of throwing from the generator.

## Diagnostic governance

Strong-type generator diagnostics use the shared `TCJxxxx` namespace and `TCJ.StrongTypes` range. Generator descriptors are tracked in `src/TCJ.Generators/AnalyzerReleases.Shipped.md` and `AnalyzerReleases.Unshipped.md`, while the allocated category ranges remain shared with `TCJ.Analyzers`. New generator diagnostics must be release-tracked, documented under `docs/analyzers/TCJxxxx.md`, deterministic, and collision-safe before shipping.
