## Summary

Describe the problem and the approach taken.

## Changes

-

## Compatibility

- [ ] No public API change
- [ ] Public API added or changed
- [ ] Breaking change documented
- [ ] Package validation passes against the published API baseline
- [ ] Any `CPxxxx` suppression is minimal and explained

## Validation

- [ ] Dependency security policy passes and no package source was added without review
- [ ] `dotnet restore TCJ.slnx --force-evaluate` reports no blocking advisory
- [ ] `dotnet build TCJ.slnx -c Release`
- [ ] `dotnet test TCJ.slnx -c Release --filter "Category!=SqlServer&Category!=AspNetCore"`
- [ ] Code coverage quality gate passes; new behavior has focused tests
- [ ] Mutation testing ran when applicable; the recorded baseline is valid and relevant survived mutants were reviewed
- [ ] Performance benchmarks pass for performance-sensitive changes
- [ ] No unexplained allocation regression is introduced
- [ ] Benchmark exclusions are documented
- [ ] Performance-policy changes include justification
- [ ] Generated benchmark output is not committed
- [ ] Architecture tests pass and module boundaries remain valid
- [ ] No new circular dependency is introduced
- [ ] New project references and dependency-direction changes are documented
- [ ] Infrastructure types do not leak into lower-level public APIs
- [ ] `architecture-policy` changes include justification
- [ ] `dotnet pack TCJ.slnx -c Release --no-build` when packaging or release infrastructure changes
- [ ] Deterministic-build configuration remains enabled
- [ ] Independent package builds are reproducible
- [ ] Assemblies match between independent builds
- [ ] Portable PDBs match between independent builds
- [ ] Source Link metadata matches
- [ ] NuGet package metadata matches
- [ ] New reproducibility normalization rules are narrow, documented, tested, and justified
- [ ] Generated reproducibility artifacts are not committed
- [ ] The official release uses the verified package set
- [ ] Release-integrity automation remains valid and package checksums pass
- [ ] SBOM generation and verification succeed
- [ ] All five release packages and symbol packages are represented
- [ ] Package versions and dependency relationships match generated package metadata
- [ ] Required package and dependency hashes are present
- [ ] License metadata has been reviewed
- [ ] `sbom-policy` changes include justification
- [ ] Generated SBOM output is not committed
- [ ] `SHA256SUMS` includes the versioned SBOM
- [ ] New public APIs include XML documentation
- [ ] Public API parameters, type parameters, and return values are documented
- [ ] Required package and getting-started examples compile
- [ ] Conceptual and package documentation is updated
- [ ] Internal documentation links are valid
- [ ] DocFX metadata and site builds succeed
- [ ] Public API documentation coverage does not regress
- [ ] Documentation baseline changes are narrow and justified
- [ ] Generated documentation output is not committed
- [ ] Documentation updated where required
- [ ] No secrets or generated artifacts included
- [ ] SQL Server integration tests pass
- [ ] The SQL Server container image remains pinned
- [ ] No permanent database secret is required
- [ ] Test databases are isolated
- [ ] Migrations apply successfully
- [ ] Transaction behavior is verified
- [ ] Container logs are sanitized
- [ ] Generated SQL Server integration output is not committed
- [ ] Production changes discovered by integration tests are explained

- [ ] ASP.NET Core integration tests pass
- [ ] Application startup succeeds
- [ ] Exception mapping is verified
- [ ] Production responses hide sensitive details
- [ ] Current-user behavior is verified
- [ ] Request-scope isolation is verified
- [ ] Linux and Windows ASP.NET Core integration results are green
- [ ] Test authentication remains test-only
- [ ] ASP.NET Core diagnostics are sanitized
- [ ] Generated ASP.NET Core integration output is not committed

## Related issue

Refs #
