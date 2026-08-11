# Contributing

Contributions should begin from `develop`, keep package boundaries explicit, add focused tests, and update conceptual and API documentation when behavior changes.

Read the repository [contribution guide](https://github.com/Amir-ESH/TCJ.Framework/blob/develop/CONTRIBUTING.md) before opening a pull request. Documentation-specific conventions, validated examples, baseline rules, and local preview commands are covered in [Documentation authoring](documentation-authoring.md).

For Native AOT/trimming policy or production project-setting changes, run the local non-blocking verifier:

```bash
python3 eng/verify-aot.py verify
```

The deterministic result is written to `artifacts/aot/aot-verification.json`; do not commit generated AOT verification output. CI enforcement is intentionally deferred to Important 8.

Before submitting a documentation or public API change, run:

```bash
dotnet tool restore
python3 eng/verify-documentation.py validate-config
dotnet build TCJ.slnx --configuration Release
python3 eng/verify-documentation.py verify --configuration Release --build-root src --api-root artifacts/documentation/api
dotnet docfx docfx/docfx.json --warningsAsErrors
```
