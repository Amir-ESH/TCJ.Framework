# TCJ Source Generator Development

`TCJ.Generators` uses Roslyn incremental generators. Keep pipelines deterministic, avoid mutable generator state, and prefer attribute providers over whole-compilation scans.

When adding generation scenarios:

- Add golden tests for generated output.
- Keep hint names stable.
- Report compiler diagnostics instead of throwing from the generator.
