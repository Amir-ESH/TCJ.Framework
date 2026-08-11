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

## Initial package matrix

| Package | Tier | Initial boundary |
|---|---|---|
| `TCJ.Core` | **Conditional** | `src/TCJ.Core/TCJ.Core.csproj` currently declares `<IsAotCompatible>false</IsAotCompatible>`. Native AOT consumption therefore remains conditional and is not a library-level Full compatibility claim; this issue does not change the metadata. |
| `TCJ.DependencyInjection` | **Conditional** | Framework-service registration is usable only while the convention scan set is empty. Reflection-based assembly scanning is a restricted path. |
| `TCJ.EntityFrameworkCore` | **Experimental** | EF Core NativeAOT remains upstream-experimental, and TCJ has reflection/dynamic-generic usage paths that require explicit treatment. |
| `TCJ.EntityFrameworkCore.SqlServer` | **Experimental** | The provider path inherits the upstream EF Core NativeAOT experimental boundary and has no qualifying packed-consumer evidence. |
| `TCJ.AspNetCore` | **Conditional** | The application must remain inside ASP.NET Core features that are supported by Native AOT; ASP.NET Core's AOT support is feature-dependent. |

No production package is labeled **Full** in this baseline.

## Restricted TCJ usage paths

### Convention-based dependency scanning

The following APIs select or can select runtime assembly scanning and are **restricted** for Native AOT:

- `TCJ.DependencyInjection.Extensions.ServiceCollectionExtensions.AddTcjDependencyInjection(IServiceCollection, params Assembly[])`
- `TCJ.DependencyInjection.Extensions.ServiceCollectionExtensions.AddTcjDependencyInjection(IServiceCollection, Action<TcjDependencyInjectionOptions>)` when the options add assemblies
- `TCJ.DependencyInjection.Extensions.ServiceCollectionExtensions.AddTcjDependencyInjection(IServiceCollection, TcjDependencyInjectionOptions)` when `TcjDependencyInjectionOptions` contains assemblies
- `TcjDependencyInjectionOptions.AddAssembly`, `AddAssemblies`, and `AddAssemblyContaining<TMarker>` because they opt into convention scanning

The implementation calls `Assembly.GetTypes()` to discover public concrete types. Trimming cannot infer every type that an application expects reflection to discover. The documented safe path for the current **Conditional** tier is to keep the TCJ scan set empty and register application services explicitly with the application's DI configuration. TCJ does not add preservation annotations or suppressions in this issue.

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

The command validates the policy schema and production-package inventory, compares declared project AOT settings with each package support tier, and rejects broad or unlisted `IL2xxx`/`IL3xxx` suppression patterns. A package declared **Full** fails verification if an evaluated repository project/props file explicitly sets `IsAotCompatible=false`.

Every run writes the deterministic machine-readable result to `artifacts/aot/aot-verification.json`. The report contains no timestamp or machine-specific absolute path, keeps packages and findings in stable order, and records the package, rule, offending project/props file, property, and value for each violation. Generated `artifacts/aot/` output is local evidence and must not be committed.

This verifier is intentionally a **local, non-blocking** validation command in Important 2. It is not wired into CI, release preflight, or release workflows yet; CI enforcement is deferred to Important 8.

Allowed suppressions are exceptional. A suppression must name one exact `IL2xxx` or `IL3xxx` diagnostic, the affected package, repository project/props file, MSBuild property, and a concrete reason in `warningPolicy.suppressions.allowed`. Wildcard/family suppressions and analyzer-wide disabling are rejected.

## Change policy

Changing a tier, warning rule, restriction, allowed suppression, or Full-evidence requirement changes the framework's compatibility contract. Such changes must be explicit in the pull request and justified as compatibility changes. This policy does not itself change runtime behavior or enable `PublishAot` on libraries.
