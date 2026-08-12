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
The support tier keeps the stronger Important 1 contract: **Full** requires a packed NuGet consumer to Native-AOT publish and execute successfully.

| Package | Verified library compatibility | Support tier | Current boundary |
|---|---|---|---|
| `TCJ.Core` | **Full** | **Conditional** | `src/TCJ.Core/TCJ.Core.csproj` declares `<IsAotCompatible>true</IsAotCompatible>`. The package-only `Core.Console` compatibility consumer enables the SDK AOT/trim analyzers and must build without warnings. The formal support tier remains Conditional until Important 8 records packed Native AOT publish-and-execute evidence. |
| `TCJ.DependencyInjection` | **Conditional (explicit path)** | **Conditional** | `src/TCJ.DependencyInjection/TCJ.DependencyInjection.csproj` declares `<IsAotCompatible>true</IsAotCompatible>`. The supported analyzer-clean path is `AddTcjDependencyInjection()` + `AddTcjDomainEvent<TEvent>()` + explicit Microsoft DI registrations. Convention scanning remains available but is annotated as trimming- and dynamic-code-restricted. Formal Full support still requires Important 8 packed publish-and-execute evidence. |
| `TCJ.EntityFrameworkCore` | Not claimed | **Experimental** | EF Core NativeAOT remains upstream-experimental, and TCJ has reflection/dynamic-generic usage paths that require explicit treatment. |
| `TCJ.EntityFrameworkCore.SqlServer` | Not claimed | **Experimental** | The provider path inherits the upstream EF Core NativeAOT experimental boundary and has no qualifying packed-consumer evidence. |
| `TCJ.AspNetCore` | **Full (supported Minimal API path)** | **Conditional** | `src/TCJ.AspNetCore/TCJ.AspNetCore.csproj` declares `<IsAotCompatible>true</IsAotCompatible>`. The package-only `AspNetCore.MinimalApi` consumer enables AOT/trim analysis with reflection-based JSON disabled, while `TCJ.AspNetCore.NativeAotSmoke` publishes a `CreateSlimBuilder()` host and executes success, validation, not-found, conflict, and unhandled-exception paths. MVC and other upstream-unsupported ASP.NET Core feature families are not covered, and formal Full support still requires Important 8 packed-package evidence. |

`TCJ.Core` remains the first TCJ package with a **Full library-level AOT/trimming compatibility** claim, and `TCJ.AspNetCore` now joins it for the documented supported Minimal API path. No production package is promoted to the formal **Full support tier** until the stronger packed publish-and-run evidence contract is satisfied.

### `TCJ.Core` analyzer fixture

`compatibility/Consumers/Core.Console/Core.Console.csproj` is the package-level analyzer fixture for `TCJ.Core`.
It references `TCJ.Core` only through `PackageReference`, sets `IsAotCompatible=true`, and is built by the existing package-consumer compatibility path from the candidate local NuGet feed. Because `IsAotCompatible=true` enables the SDK trimming, single-file, and AOT analyzers, warnings surface during the normal consumer build instead of being hidden behind project references.

The fixture is intentionally compile/runtime-check only and does not set `PublishAot=true` or `PublishTrimmed=true`. Native AOT publish-and-execute release evidence remains the responsibility of Important 8.

## Restricted TCJ usage paths

### TCJ.DependencyInjection explicit path and convention scanning

`TCJ.DependencyInjection` declares `IsAotCompatible=true`. Its supported Native AOT/trimming path is explicit and has three parts:

```csharp
services.AddTcjDependencyInjection();
services.AddTcjDomainEvent<OrderPlaced>();
services.AddTransient<IDomainEventHandler<OrderPlaced>, OrderPlacedHandler>();
```

The parameterless `AddTcjDependencyInjection()` overload registers `TimeProvider`, `IGuidGenerator`, and `IDomainEventDispatcher` without assembly enumeration. `AddTcjDomainEvent<TEvent>()` declares a closed generic dispatch route for each event type; it performs no handler discovery. Application services and handlers are then registered with normal Microsoft DI APIs, so their chosen transient/scoped/singleton lifetimes remain consumer-controlled. Domain-event dispatch on this path resolves the closed generic handler collection directly and does not construct generic types from runtime `Type` values.

`compatibility/Consumers/DependencyInjection.AotSafe.Console/DependencyInjection.AotSafe.Console.csproj` consumes the packed `TCJ.Core` and `TCJ.DependencyInjection` packages, sets `IsAotCompatible=true`, registers a closed event route and handler explicitly, and dispatches a real event. Its normal compatibility build therefore exercises the supported call sites with SDK trim/AOT analyzers enabled. The fixture intentionally remains compile/runtime-check only; Important 8 owns packed `PublishAot=true` publish-and-execute evidence.

The following APIs select or can select runtime assembly scanning and are **restricted** for Native AOT:

- `TCJ.DependencyInjection.Extensions.ServiceCollectionExtensions.AddTcjDependencyInjection(IServiceCollection, params Assembly[])`
- `TCJ.DependencyInjection.Extensions.ServiceCollectionExtensions.AddTcjDependencyInjection(IServiceCollection, Action<TcjDependencyInjectionOptions>)`
- `TCJ.DependencyInjection.Extensions.ServiceCollectionExtensions.AddTcjDependencyInjection(IServiceCollection, TcjDependencyInjectionOptions)`
- `TcjDependencyInjectionOptions.AddAssembly`, `AddAssemblies`, and `AddAssemblyContaining<TMarker>` because they opt into convention scanning

The three scanner-capable `AddTcjDependencyInjection` overloads are annotated with both `RequiresUnreferencedCode` and `RequiresDynamicCode`. They keep the existing `Assembly.GetTypes()` convention discovery and a restricted runtime-generic domain-event dispatch fallback for regular JIT/non-trimmed applications. Trimming/Native AOT callers therefore see the platform restrictions at the public API boundary instead of receiving a silent preservation workaround. TCJ adds no linker-preservation XML and no broad trim/AOT warning suppression for scanning.

### ASP.NET Core supported Native AOT path

`TCJ.AspNetCore` declares `IsAotCompatible=true` and supports the TCJ-owned integration surface when the application stays inside ASP.NET Core's Native-AOT-supported Minimal API feature set. The supported TCJ path uses `WebApplication.CreateSlimBuilder()` (or the current framework-supported equivalent), `AddTcjAspNetCore()`, `UseTcjAspNetCore()`, Minimal API endpoints, framework Problem Details, current-user resolution, and TCJ health endpoints.

TCJ-owned JSON paths do not depend on reflection metadata. Health responses use an internal source-generated `JsonSerializerContext`, and `AddTcjAspNetCore()` contributes TCJ's generated Problem Details metadata to the ASP.NET Core HTTP JSON resolver chain. Native AOT applications remain responsible for source-generating metadata for their own response DTOs and for custom object types placed in `ResultError.Metadata`.

Two fixtures verify different layers without conflating them:

- `compatibility/Consumers/AspNetCore.MinimalApi` consumes the packed `TCJ.Core`, `TCJ.DependencyInjection`, and `TCJ.AspNetCore` candidates, sets `IsAotCompatible=true`, disables reflection-based System.Text.Json defaults, and exercises the supported Minimal API call sites under SDK AOT/trim analysis. It intentionally does not set `PublishAot`; Important 8 owns packed-package publish evidence.
- `tests/TCJ.AspNetCore.NativeAotSmoke` is a project-reference executable with `PublishAot=true`. The ASP.NET Core integration workflow publishes it for `linux-x64`, starts the native host, and verifies success, validation/bad-request, not-found, conflict, and unhandled-exception behavior, including that the exception detail is not leaked. This proves the package source is Native-AOT-compatible before Important 8 turns packed artifacts into a release guarantee.

This compatibility claim does **not** extend to MVC controllers or other ASP.NET Core feature families that the platform does not support for Native AOT. TCJ adds no controller-generation layer, Newtonsoft.Json dependency, or preservation descriptor to change those upstream boundaries.

### EF Core reflection and dynamic generic paths

The following public paths are restricted in addition to EF Core's upstream NativeAOT limitations:

- `ModelBuilderExtensions.RegisterEntityTypeConfiguration(ModelBuilder, params Assembly[])` discovers configuration types and invokes generic configuration methods dynamically.
- `ModelBuilderExtensions.RegisterAllEntities<TBaseType>(ModelBuilder, params Assembly[])` discovers entity types through `Assembly.GetExportedTypes()`.
- `EntitySearcher.ExistsAsync(...)` and `EntitySearcher.FindAsync(...)` construct closed generic executor types from runtime EF model metadata.
- `OutboxServiceCollectionExtensions.AddTcjOutbox<TDbContext>(...)` can fall back to scanning loaded assemblies when resolving an event type that was not explicitly registered. A Native AOT experiment must register persisted event contracts explicitly with `AddTcjOutboxEvent<TEvent>` and must not rely on fallback discovery.

These restrictions are recorded by exact API in `eng/aot-policy.json`; they are not package-wide blanket exemptions.

## Upstream boundaries

EF Core documentation currently describes NativeAOT and precompiled queries as experimental and not yet suited for production use. It also documents limitations including unsupported dynamic queries and provider participation in precompiled-query support. For that reason neither `TCJ.EntityFrameworkCore` nor `TCJ.EntityFrameworkCore.SqlServer` can be promoted to **Full** merely because TCJ code compiles cleanly.

ASP.NET Core Native AOT support is feature-dependent. A TCJ application that uses `TCJ.AspNetCore` must remain inside the upstream-supported feature set; for example, ASP.NET Core documents Minimal APIs as partially supported while MVC, Blazor Server, and SignalR are not supported in Native AOT. That upstream boundary is why the initial package tier is **Conditional**, not **Full**.

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

The command validates the policy schema and production-package inventory, compares declared project AOT settings with each package support tier, and rejects broad or unlisted `IL2xxx`/`IL3xxx` suppression patterns. A package declared **Full** fails verification if an evaluated repository project/props file explicitly sets `IsAotCompatible=false`. It also validates the package-level analyzer fixtures for `TCJ.Core`, `TCJ.DependencyInjection`, and `TCJ.AspNetCore`: each production package must declare `IsAotCompatible=true`, each fixture must enable `IsAotCompatible`, consume the exact expected packed TCJ package closure, contain no project reference, and remain compile-only rather than taking over Important 8's packed publish responsibility. The separate ASP.NET Core integration verifier additionally pins the project-reference Native AOT smoke host to `PublishAot=true`, reflection-free JSON defaults, the required HTTP scenarios, and a Minimal-API-only surface.

Every run writes the deterministic machine-readable result to `artifacts/aot/aot-verification.json`. The report contains no timestamp or machine-specific absolute path, keeps packages and findings in stable order, and records the package, rule, offending project/props file, property, and value for each violation. Generated `artifacts/aot/` output is local evidence and must not be committed.

This verifier is intentionally a **local, non-blocking** validation command in Important 2. It is not wired into CI, release preflight, or release workflows yet; CI enforcement is deferred to Important 8.

Allowed suppressions are exceptional. A suppression must name one exact `IL2xxx` or `IL3xxx` diagnostic, the affected package, repository project/props file, MSBuild property, and a concrete reason in `warningPolicy.suppressions.allowed`. Wildcard/family suppressions and analyzer-wide disabling are rejected.

## Change policy

Changing a tier, warning rule, restriction, allowed suppression, or Full-evidence requirement changes the framework's compatibility contract. Such changes must be explicit in the pull request and justified as compatibility changes. This policy does not itself change runtime behavior or enable `PublishAot` on libraries.
