# TCJ.Analyzers

`TCJ.Analyzers` provides compile-time diagnostics and code fixes for TCJ Framework applications without adding runtime dependencies to the application.

This infrastructure package currently contains the analyzer and code-fix assemblies but intentionally ships no TCJ diagnostic rule yet.

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
