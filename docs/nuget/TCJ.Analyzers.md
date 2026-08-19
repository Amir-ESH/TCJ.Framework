# TCJ.Analyzers

`TCJ.Analyzers` provides compile-time diagnostics and code fixes for TCJ Framework applications without adding runtime dependencies to the application.

The package currently includes TCJ dependency-injection diagnostics and their safe code fixes. Analyzer assets remain compile-time-only and do not add runtime dependencies to the application.

## Diagnostics

- `TCJ0001` reports effectively public concrete dependencies with conflicting TCJ lifetime markers.
- `TCJ0002` reports interface-registration markers that expose no eligible service contract.
- `TCJ0003` reports convention-marked concrete dependencies that are not effectively public. A nested dependency is scan-eligible only when it and every containing type are `public`; the automatic accessibility fix is offered only when changing the marked type itself is sufficient.
- `TCJ0004` reports domain-event handlers that also carry TCJ dependency lifetime markers. Convention-scanned handlers are registered by the handler pipeline, so those markers do not control handler lifetime; the code fix removes only directly declared TCJ lifetime markers.

See the repository analyzer reference for detailed causes, fixes, suppression guidance, and compatibility notes.

## Package model

- Analyzer assets are installed under `analyzers/dotnet/cs`.
- No analyzer assembly is exposed as a `lib/` runtime asset.
- Roslyn compiler/workspace dependencies are authoring dependencies and are not runtime package dependencies.
- Runtime TCJ symbols are resolved through Roslyn metadata symbols rather than direct runtime project references from analyzer code.

## Local validation

From the repository root:

```bash
dotnet test tests/TCJ.Analyzers.Tests/TCJ.Analyzers.Tests.csproj -c Release
dotnet pack eng/packaging/TCJ.Analyzers/TCJ.Analyzers.Package.csproj -c Release
```

The analyzer tests also restore the packed package into an isolated application and verify that analyzer, code-fix, and Roslyn implementation DLLs are not copied to runtime output.

## Documentation

- [Analyzer development boundaries](https://github.com/Amir-ESH/TCJ.Framework/blob/develop/docs/analyzer-development.md)
- [Contributing](https://github.com/Amir-ESH/TCJ.Framework/blob/develop/CONTRIBUTING.md)
- [Repository](https://github.com/Amir-ESH/TCJ.Framework)
- [Issues](https://github.com/Amir-ESH/TCJ.Framework/issues)

## License

TCJ Framework is licensed under GNU LGPL v3.0 only (`LGPL-3.0-only`). See the repository license for details.
