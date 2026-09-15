# TCJ.Messaging.Contracts

`TCJ.Messaging.Contracts` is the optional schema-governance package for explicit `TCJ.Messaging` message contracts. It generates deterministic JSON Schema Draft 2020-12 artifacts from the same `JsonTypeInfo` metadata used by runtime serialization, computes SHA-256 wire fingerprints, analyzes schema compatibility conservatively, validates representative examples and existing `IMessageUpcaster` paths, and verifies released-version immutability.

## Install

```bash
dotnet add package TCJ.Messaging.Contracts --prerelease
```

The package depends on `TCJ.Messaging`. `TCJ.Messaging` does not depend on this package, and governance is not required on the runtime messaging hot path.

## Core APIs

- `MessageContractSchemaGenerator`
- `MessageContractArtifactGenerator`
- `MessageContractCompatibilityAnalyzer`
- `MessageContractExampleValidator`
- `MessageUpcasterGraphValidator`
- `MessageContractBaselineValidator`
- `MessageContractManifestSerializer`

Schema compatibility and runtime version registration are distinct. The package does not add automatic downcasting, remote schema-registry access, or semantic migration.

See [Message contract governance](../message-contract-governance.md) for versioning, compatibility direction, baseline provenance, classifications, and CI guidance.
