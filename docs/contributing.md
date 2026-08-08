# Contributing

Contributions should begin from `develop`, keep package boundaries explicit, add focused tests, and update conceptual and API documentation when behavior changes.

Read the repository [contribution guide](https://github.com/Amir-ESH/TCJ.Framework/blob/develop/CONTRIBUTING.md) before opening a pull request. Documentation-specific conventions, validated examples, baseline rules, and local preview commands are covered in [Documentation authoring](documentation-authoring.md).

Before submitting a documentation or public API change, run:

```bash
dotnet tool restore
python3 eng/verify-documentation.py validate-config
dotnet build TCJ.slnx --configuration Release
python3 eng/verify-documentation.py verify --configuration Release --build-root src --api-root artifacts/documentation/api
dotnet docfx docfx/docfx.json --warningsAsErrors
```
