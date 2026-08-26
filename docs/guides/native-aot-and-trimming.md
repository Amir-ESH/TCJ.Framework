# Native AOT and trimming compatibility

TCJ treats Native AOT and trimming compatibility as a product contract. A library property alone is not enough to claim that an application can safely publish and run as Native AOT.

The machine-readable baseline is `eng/aot-policy.json`. Policy changes are compatibility changes and must be reviewed explicitly.

## Support tiers

| Tier | TCJ contract |
|---|---|
| **Full** | A supported usage path has evidence from a **Packed NuGet** consumer that publishes with `PublishAot=true`, produces no TCJ-caused trimming/AOT warnings, and executes successfully. |
| **Conditional** | The package is supported only through documented safe paths or while respecting a named upstream restriction. |
| **Experimental** | TCJ may exercise the path, but does not promise production Native AOT support. |
| **Unsupported** | TCJ does not support Native AOT consumption for that package or usage path. |

A package is not promoted to **Full** from project-reference builds. The minimum evidence must consume the candidate `.nupkg`, prove that no TCJ project reference is used, publish a real application with `PublishAot=true`, execute the published native binary, and report zero TCJ-caused trimming and AOT warnings.

## `IsAotCompatible` is not `PublishAot`

`IsAotCompatible` is a **library compatibility declaration**. On supported target frameworks, setting it to `true` enables the library-side AOT/trimming analyzers and related compatibility metadata. It does not Native-AOT-publish a class library and it does not prove that every application feature using that library is AOT-safe.

`PublishAot=true` is an **application publish setting**. The application and its full dependency closure are analyzed and compiled as Native AOT during publish. TCJ therefore bases support claims on packed-package consumption by a real application, not only on library builds or project references.

TCJ does not enable `PublishAot` on production library projects as part of this policy.

## Package matrix

The matrix separates **verified library compatibility** from the repository's end-to-end **support tier**.
The library-compatibility column answers whether a package itself is declared and analyzer-verified as AOT/trim compatible.
The support tier keeps the stronger the original Native AOT support contract contract: **Full** requires a packed NuGet consumer to Native-AOT publish and execute successfully.

| Package | Verified library compatibility | Support tier | Current boundary |
|---|---|---|---|
| `TCJ.Core` | **Full** | **Full** | `src/TCJ.Core/TCJ.Core.csproj` declares `<IsAotCompatible>true</IsAotCompatible>`. The package analyzer fixture stays clean, and the blocking `linux-x64` packaged-consumer gate restores the exact candidate package, publishes it with `PublishAot=true`, executes the native binary, and verifies the loaded `PackageVersion`. |
| `TCJ.DependencyInjection` | **Full (explicit path)** | **Full** | Full support covers `AddTcjDependencyInjection()` + `AddTcjDomainEvent<TEvent>()` + explicit Microsoft DI registrations. The packed native smoke executes this path. Convention scanning remains available for JIT consumers but is individually annotated and excluded from the Full AOT contract. |
| `TCJ.EntityFrameworkCore` | **Experimental static path** | **Experimental** | A project-reference NativeAOT fixture uses EF compiled-model/query-precompile tooling and avoids TCJ runtime-discovery APIs. Reflection-driven model discovery, runtime entity search, the outbox fallback resolver, and TCJ soft-delete global filters are outside the experiment; upstream EF NativeAOT is not production-ready. |
| `TCJ.EntityFrameworkCore.SqlServer` | **Experimental provider path** | **Experimental** | The experimental fixture configures SQL Server and TCJ rowversion conventions while EF generates a compiled model and precompiled queries. Provider support inherits both EF Core's experimental provider boundary and the provider-neutral compiled-model limitations; there is no qualifying packed-consumer evidence. |
| `TCJ.AspNetCore` | **Full (supported Minimal API path)** | **Full** | Full support covers the documented Minimal API path. The blocking packed-package smoke uses `CreateSlimBuilder()`, starts the native host, verifies representative success/error behavior, and confirms the candidate package version is the version loaded at runtime. MVC and other upstream-unsupported ASP.NET Core feature families remain outside the contract. |

`TCJ.Core`, the explicit AOT-safe `TCJ.DependencyInjection` path, and the documented `TCJ.AspNetCore` Minimal API path now have the stronger **Full** support tier because the same locally packed release-candidate application publishes and executes them as Native AOT. The EF packages remain separately **Experimental**.

### `TCJ.Core` analyzer fixture

`compatibility/Consumers/Core.Console/Core.Console.csproj` is the package-level analyzer fixture for `TCJ.Core`.
It references `TCJ.Core` only through `PackageReference`, sets `IsAotCompatible=true`, and is built by the existing package-consumer compatibility path from the candidate local NuGet feed. Because `IsAotCompatible=true` enables the SDK trimming, single-file, and AOT analyzers, warnings surface during the normal consumer build instead of being hidden behind project references.

The fixture is intentionally compile/runtime-check only and does not set `PublishAot=true` or `PublishTrimmed=true`. The stronger publish-and-execute guarantee is provided by the packaged Native AOT release gate below.

## Packaged Native AOT release guarantee

`smoke/TCJ.NativeAot.SmokeTest` is the release-contract consumer for the current Full package set: `TCJ.Core`, `TCJ.DependencyInjection`, and `TCJ.AspNetCore`. It contains **no TCJ `ProjectReference`**. `smoke/NuGet.Config` source-maps `TCJ.*` to `artifacts/packages`, while the runner uses an isolated NuGet package cache so the smoke cannot silently resolve a previously installed TCJ package.

The supported CI runtime identifier is **`linux-x64`**. `eng/run-native-aot-smoke.py` restores the exact candidate version, verifies the resolved TCJ dependency closure and local package-source identity, publishes the smoke with `PublishAot=true`, and executes the native binary. The application exercises the reflection-free DI bootstrap and domain-event dispatch together with representative ASP.NET Core success, validation, not-found, conflict, and unhandled-exception behavior. At process startup it reports each loaded TCJ assembly's `PackageVersion`; the runner requires those runtime versions to equal the candidate package version.

The Full release guarantee accepts **no trim/AOT warning-count baseline**. Any `IL2xxx` or `IL3xxx` diagnostic observed during packed publish fails the smoke. The deterministic execution result is written to `artifacts/aot/native-aot-smoke/native-aot-result.json`, and `python3 eng/verify-aot.py verify-result --version <candidate-version>` checks that result against the current Full support-tier set and exact `.nupkg` files. This makes a support-tier promotion fail closed if executable evidence does not move with it.

The blocking gate runs in normal CI, release preflight, the tagged release build, and again when the release publishing job consumes the retained result. Tagged releases retain the smoke logs/result plus `aot-runtime-verification.json` as Native AOT release evidence.

The EF NativeAOT fixture is intentionally **not** part of this Full release guarantee. `TCJ.EntityFrameworkCore` and `TCJ.EntityFrameworkCore.SqlServer` remain Experimental and continue to record upstream/experimental limitations separately. Their warnings cannot be used as an accepted baseline for the non-EF Full gate.

## Restricted TCJ usage paths

### TCJ.DependencyInjection explicit path and convention scanning

`TCJ.DependencyInjection` declares `IsAotCompatible=true`. Its supported Native AOT/trimming path is explicit and has three parts:

```csharp
services.AddTcjDependencyInjection();
services.AddTcjDomainEvent<OrderPlaced>();
services.AddTransient<IDomainEventHandler<OrderPlaced>, OrderPlacedHandler>();
```

The parameterless `AddTcjDependencyInjection()` overload registers `TimeProvider`, `IGuidGenerator`, and `IDomainEventDispatcher` without assembly enumeration. `AddTcjDomainEvent<TEvent>()` declares a closed generic dispatch route for each event type; it performs no handler discovery. Application services and handlers are then registered with normal Microsoft DI APIs, so their chosen transient/scoped/singleton lifetimes remain consumer-controlled. Domain-event dispatch on this path resolves the closed generic handler collection directly and does not construct generic types from runtime `Type` values.

`compatibility/Consumers/DependencyInjection.AotSafe.Console/DependencyInjection.AotSafe.Console.csproj` consumes the packed `TCJ.Core` and `TCJ.DependencyInjection` packages, sets `IsAotCompatible=true`, registers a closed event route and handler explicitly, and dispatches a real event. Its normal compatibility build therefore exercises the supported call sites with SDK trim/AOT analyzers enabled. The packaged Native AOT release smoke then publishes and executes the same explicit registration/dispatch contract from locally packed candidates.

The following APIs select or can select runtime assembly scanning and are **restricted** for Native AOT:

- `TCJ.DependencyInjection.Extensions.ServiceCollectionExtensions.AddTcjDependencyInjection(IServiceCollection, params Assembly[])`
- `TCJ.DependencyInjection.Extensions.ServiceCollectionExtensions.AddTcjDependencyInjection(IServiceCollection, Action<TcjDependencyInjectionOptions>)`
- `TCJ.DependencyInjection.Extensions.ServiceCollectionExtensions.AddTcjDependencyInjection(IServiceCollection, TcjDependencyInjectionOptions)`
- `TcjDependencyInjectionOptions.AddAssembly`, `AddAssemblies`, and `AddAssemblyContaining<TMarker>` because they opt into convention scanning

The three scanner-capable `AddTcjDependencyInjection` overloads are annotated with both `RequiresUnreferencedCode` and `RequiresDynamicCode`. They keep the existing `Assembly.GetTypes()` convention discovery and a restricted runtime-generic domain-event dispatch fallback for regular JIT/non-trimmed applications. Trimming/Native AOT callers therefore see the platform restrictions at the public API boundary instead of receiving a silent preservation workaround. TCJ adds no linker-preservation XML and no broad trim/AOT warning suppression for scanning.

### ASP.NET Core supported Native AOT path

`TCJ.AspNetCore` declares `IsAotCompatible=true` and supports the TCJ-owned integration surface when the application stays inside ASP.NET Core's Native-AOT-supported Minimal API feature set. The supported TCJ path uses `WebApplication.CreateSlimBuilder()` (or the current framework-supported equivalent), `AddTcjAspNetCore()`, `UseTcjAspNetCore()`, Minimal API endpoints, framework Problem Details, current-user resolution, and TCJ health endpoints.

TCJ-owned JSON paths do not depend on reflection metadata. Health responses use an internal source-generated `JsonSerializerContext`, and `AddTcjAspNetCore()` contributes TCJ's generated Problem Details metadata to the ASP.NET Core HTTP JSON resolver chain. Native AOT applications remain responsible for source-generating metadata for their own response DTOs and for custom object types placed in `ResultError.Metadata`.

Three fixtures/gates verify different layers without conflating them:

- `compatibility/Consumers/AspNetCore.MinimalApi` consumes the packed `TCJ.Core`, `TCJ.DependencyInjection`, and `TCJ.AspNetCore` candidates, sets `IsAotCompatible=true`, disables reflection-based System.Text.Json defaults, and exercises the supported Minimal API call sites under SDK AOT/trim analysis.
- `tests/TCJ.AspNetCore.NativeAotSmoke` remains a project-reference integration fixture with `PublishAot=true`; it catches source-level ASP.NET Core AOT regressions early.
- `smoke/TCJ.NativeAot.SmokeTest` is the stronger blocking release-contract fixture. It consumes only locally packed TCJ NuGets, publishes for `linux-x64`, executes the native application, validates representative HTTP/error and domain-event behavior, and proves the loaded TCJ package versions are the candidate versions.

This compatibility claim does **not** extend to MVC controllers or other ASP.NET Core feature families that the platform does not support for Native AOT. TCJ adds no controller-generation layer, Newtonsoft.Json dependency, or preservation descriptor to change those upstream boundaries.

### EF Core experimental NativeAOT path

`TCJ.EntityFrameworkCore` and `TCJ.EntityFrameworkCore.SqlServer` remain **Experimental** for NativeAOT. the EF Core Native AOT experimental fixture adds evidence for a deliberately narrow static path; it does not turn EF Core NativeAOT into a production-supported TCJ feature.

The repository fixture `tests/TCJ.EntityFrameworkCore.NativeAotExperimental` is a project-reference executable. It sets `PublishAot=true` and a concrete `linux-x64` runtime identifier, references `Microsoft.EntityFrameworkCore.Tasks`, enables the EF Core 10 `EFOptimizeContext` integration, opts into `Microsoft.EntityFrameworkCore.GeneratedInterceptors`, and generates the compiled model plus precompiled queries during publish. The fixture configures the SQL Server provider, explicitly registers a Guid Strong ID through `StrongIdConversionRegistry`, applies `ApplyStrongIdConversions(...)` and `ApplyTcjSqlServerConventions()`, contains a representative statically analyzable LINQ query, publishes as Native AOT, and starts without opening a database connection. Its fixture-local Strong ID mirrors the closed conversion-expression shape emitted by `TCJ.Generators`; the generator output itself is covered by generator tests and the EF/SQL Server suites exercise real generated Strong IDs. This separation avoids depending on analyzer-generated members during EF Core's secondary `MSBuildWorkspace` query-precompilation compilation while keeping the TCJ-owned registry/model path under NativeAOT analysis. Strong ID registration uses closed expressions and model metadata only; it does not scan assemblies or activate generic converter types from runtime `Type` values.

For an EF Core 10 NativeAOT application, the relevant project settings are:

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <RuntimeIdentifier>linux-x64</RuntimeIdentifier>
  <EFOptimizeContext>true</EFOptimizeContext>
  <EFScaffoldModelStage>publish</EFScaffoldModelStage>
  <EFPrecompileQueriesStage>publish</EFPrecompileQueriesStage>
  <InterceptorsNamespaces>$(InterceptorsNamespaces);Microsoft.EntityFrameworkCore.GeneratedInterceptors</InterceptorsNamespaces>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Microsoft.EntityFrameworkCore.Tasks">
    <PrivateAssets>all</PrivateAssets>
    <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
  </PackageReference>
</ItemGroup>
```

`Microsoft.EntityFrameworkCore.Tasks` is a consumer build dependency rather than a TCJ runtime dependency. Normal JIT applications do not need `PublishAot`, compiled-model generation, or precompiled-query tooling merely because they reference a TCJ EF package.

The following TCJ APIs are **restricted** on the NativeAOT path:

- `ModelBuilderExtensions.RegisterEntityTypeConfiguration(...)` scans assemblies and closes generic configuration methods from runtime `Type` values. It is annotated with `RequiresUnreferencedCode` and `RequiresDynamicCode`.
- `ModelBuilderExtensions.RegisterAllEntities<TBaseType>(...)` discovers entity types at runtime. It is annotated with `RequiresUnreferencedCode` and `RequiresDynamicCode`.
- `ModelBuilderExtensions.GetModuleAssemblies()` performs runtime module-assembly discovery and is annotated with `RequiresUnreferencedCode`.
- `SoftDeleteModelBuilderExtensions.ApplySoftDeleteQueryFilters()` is outside the NativeAOT experiment because it installs EF global query filters, and the compiled-model path required by current EF NativeAOT does not support global query filters. Normal JIT soft-delete behavior is unchanged.
- `IEntitySearcher.ExistsAsync(...)` and `FindAsync(...)` construct runtime entity-specific predicates/executors and are annotated with `RequiresUnreferencedCode` and `RequiresDynamicCode`. NativeAOT consumers should prefer statically typed repository or `DbContext` queries that EF tooling can precompile.
- The transactional-outbox convention resolver can scan loaded assemblies for a persisted event name that was not explicitly registered. An AOT experiment must register persisted event contracts with `AddTcjOutboxEvent<TEvent>` and provide source-generated `System.Text.Json` metadata for event payload types. The the EF Core Native AOT experimental fixture fixture does **not** claim the outbox path.

TCJ-owned static-path cleanup avoids runtime `ModelBuilder.Entity(Type)` calls in the SQL Server rowversion convention, and the default outbox serializer now resolves `JsonTypeInfo` before calling the metadata-based `JsonSerializer` overloads. To preserve existing JIT consumers, the serializer installs `DefaultJsonTypeInfoResolver` only when `JsonSerializer.IsReflectionEnabledByDefault` is true; Native AOT/reflection-disabled applications must provide source-generated event metadata instead. The normal JIT soft-delete implementation is deliberately left unchanged because EF compiled models currently exclude global query filters rather than providing an AOT-safe equivalent.

`eng/verify-aot.py` validates the fixture contract with `AOT007`. Missing `Microsoft.EntityFrameworkCore.Tasks`, `EFOptimizeContext`, publish stages, generated-interceptor namespace, concrete RID, or representative static query/provider setup produces an actionable configuration failure before CI attempts NativeAOT publish. The SQL Server integration workflow then publishes and executes the experimental native fixture.

This evidence is intentionally weaker than a support-tier upgrade: it uses project references and does not execute a packaged NuGet consumer against a real database. A future upgrade beyond **Experimental** requires packaged-consumer publish-and-execute evidence under the repository AOT policy.

## Upstream boundaries

EF Core documentation currently describes NativeAOT and precompiled queries as experimental and not yet suited for production use. It documents unsupported dynamic queries, provider participation in precompiled-query support, and compiled-model limitations such as global query filters, lazy-loading/change-tracking proxies, and custom model-cache keys. For that reason neither `TCJ.EntityFrameworkCore` nor `TCJ.EntityFrameworkCore.SqlServer` can be promoted to **Full** merely because TCJ code compiles cleanly.

ASP.NET Core Native AOT support is feature-dependent. TCJ's **Full** tier therefore applies only to the explicitly documented and packaged-executed Minimal API path; it does not convert upstream-unsupported feature families such as MVC controllers into supported Native AOT scenarios.

References:

- [.NET Native AOT deployment and `IsAotCompatible`](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
- [EF Core NativeAOT and precompiled queries](https://learn.microsoft.com/ef/core/performance/nativeaot-and-precompiled-queries)
- [ASP.NET Core Native AOT support](https://learn.microsoft.com/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0)

## Warning policy

For a **Full** support claim, TCJ-caused trim and AOT diagnostics are errors: qualifying evidence must contain zero such warnings. For **Conditional** paths, any TCJ-caused warning must map to a named restriction in the policy; undocumented warnings fail the claim. For **Experimental** paths, warnings must be recorded and reviewed, but their presence does not create a production-support promise.

Warning suppressions cannot be used to manufacture a support claim. This policy issue adds no suppressions.

## Promotion to Full

Before changing a package tier to **Full**, the compatibility change must include evidence that:

1. a consumer restores the candidate TCJ package from packed NuGet output rather than a TCJ project reference;
2. the consumer sets `PublishAot=true` and Native AOT publish succeeds;
3. the published native binary executes the supported scenario successfully;
4. the publish produces zero TCJ-caused trimming warnings and zero TCJ-caused AOT warnings; and
5. the evidence covers at least one documented consumer scenario for the package and names any remaining restricted public paths individually.

A project-reference build, a clean library build, or setting `IsAotCompatible=true` without packed-consumer publish-and-run evidence is insufficient.

## Local policy verification

Run the repository-native verifier before opening a pull request that changes AOT policy, production package project settings, or warning configuration:

```bash
python3 eng/verify-aot.py verify
```

The command validates the policy schema and production-package inventory, compares declared project AOT settings with each package support tier, rejects broad or unlisted `IL2xxx`/`IL3xxx` suppression patterns, validates the package-level analyzer fixtures, and separately validates the experimental EF NativeAOT fixture. It also validates the the packed Native AOT release smoke packed smoke contract, supported `linux-x64` RID, exact current Full package closure, local-only TCJ package source mapping, absence of TCJ project references, and blocking wiring in CI/release workflows. A package declared **Full** fails verification if its policy evidence no longer points at the executable packed-package result.

Every policy run writes the deterministic machine-readable result to `artifacts/aot/aot-verification.json`. Runtime evidence is independently verified with `verify-result` and written to `artifacts/aot/aot-runtime-verification.json`. Reports contain no timestamp or machine-specific absolute path and keep findings in stable order. Generated `artifacts/aot/` output must not be committed.

AOT policy verification is now **blocking** in `ci.yml`, `release-preflight.yml`, and `release.yml`. The release build runs the packed native application and retains its result; the publish job downloads that retained evidence and re-runs `verify-result` against the exact release package set before NuGet publishing.

Allowed suppressions are exceptional. A suppression must name one exact `IL2xxx` or `IL3xxx` diagnostic, the affected package, repository project/props file, MSBuild property, and a concrete reason in `warningPolicy.suppressions.allowed`. Wildcard/family suppressions and analyzer-wide disabling are rejected.

## Change policy

Changing a tier, warning rule, restriction, allowed suppression, or Full-evidence requirement changes the framework's compatibility contract. Such changes must be explicit in the pull request and justified as compatibility changes. This policy does not itself change runtime behavior or enable `PublishAot` on libraries.
