# TCJ.Messaging.Contracts

Optional deterministic JSON schema and schema-evolution governance for contracts registered by `TCJ.Messaging`.

```bash
dotnet add package TCJ.Messaging.Contracts --prerelease
```

The package generates Draft 2020-12 schemas from explicit runtime `JsonTypeInfo`, canonicalizes and fingerprints wire schemas with SHA-256, analyzes Backward/Forward/Full compatibility conservatively, validates examples and existing `IMessageUpcaster` paths, and checks immutable release-derived baselines.

It depends on `TCJ.Messaging`; the reverse dependency is intentionally prohibited. No broker SDK, hosted schema registry, runtime network access, automatic downcasting, or non-JSON serializer governance is introduced.

See [Message contract governance](../message-contract-governance.md).
