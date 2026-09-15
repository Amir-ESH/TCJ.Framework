# Message contract governance

`TCJ.Messaging.Contracts` is the optional build/test/CI governance layer for the runtime contracts owned by `TCJ.Messaging`. It does **not** replace `IMessageContractRegistry`, add a remote registry, or participate in the normal publish/receive hot path.

## Runtime source of truth

The runtime identity remains `MessageType + MessageVersion`. `MessageVersion` is a positive integer and is independent from the NuGet package SemVer. CLR assembly-qualified names are not wire identities.

Applications continue to register the exact serialization metadata used at runtime:

```csharp
services.AddTcjMessaging();
services.AddTcjMessage(
    "order.submitted",
    2,
    AppJsonContext.Default.OrderSubmitted);
```

The governance package accepts the resulting `MessagingMessageContract` and generates JSON Schema from its explicit `JsonTypeInfo`. Reflection scanning is not introduced as a registration mechanism.

## Schema and fingerprint

Step 52 pins JSON Schema Draft 2020-12 and canonicalization version `1`. Canonical output uses UTF-8, deterministic object-key ordering, deterministic `required` ordering, and no insignificant whitespace. The wire fingerprint is SHA-256 over the canonical schema bytes only.

Ownership, deprecation, migration notes, semantic review notes, examples, and data classifications are deliberately excluded from the wire fingerprint. Changing those fields does not create a false wire change.

A typical consumer-selected output root is:

```text
contracts/
  manifest.json
  order.submitted/
    v1/
      schema.json
      contract.json
      fingerprint.sha256
      examples/
        valid-minimal.json
```

TCJ.Framework does not ship fake application business contracts under a repository `contracts/` directory. Repository tests and tooling use synthetic fixture identities only.

## Published-version immutability

Once an official release baseline contains `order.submitted@1`, that identity and wire-schema fingerprint are immutable. A changed wire shape requires a new positive `MessageVersion`.

Release-derived baselines record `releaseVersion`, `sourceTag`, and the full `sourceCommit`. `MessageContractBaselineValidator` rejects deletion or fingerprint replacement of an already-published identity. Documentation-only metadata may change without changing the wire fingerprint.

## Compatibility direction

The compatibility modes use standard schema-registry direction terminology:

| Mode | Meaning |
| --- | --- |
| `Backward` | the new reader can read payloads written by the previous producer schema |
| `Forward` | the previous reader can read payloads written by the new producer schema |
| `Full` | both Backward and Forward hold |
| `*Transitive` | the corresponding comparison is made against every retained published version |

The analyzer returns `Compatible`, `Breaking`, or `ReviewRequired`. Unsupported or uncertain schema constructs are never reported as safe. `ReviewRequired` fails unattended governance unless the exact finding code has a bounded, explicit reviewed justification. `Breaking` cannot be overridden by review metadata.

Structural schema compatibility does not prove semantic compatibility. A field can keep the same JSON type while changing business meaning, units, timezone interpretation, partition semantics, or lifecycle semantics. New versions therefore support `changeSummary`, `migrationNotes`, and `semanticCompatibilityNotes` for human review.

## Runtime version caveat and upcasters

Schema compatibility does not perform runtime version negotiation. A consumer registered only for version `1` does not automatically accept an envelope marked version `2` merely because the schemas are structurally compatible.

TCJ continues to use the existing `IMessageUpcaster` abstraction for explicit older-to-newer payload upgrades. Governance validates graph uniqueness, required paths, representative old examples, bounded output, and target schema/runtime deserialization. Step 52 adds no downcaster and does not let upcaster availability change the structural compatibility result.

## Examples and sensitive data

Governed public integration contracts can require representative examples. Examples are validated against the generated schema and then deserialized with the same runtime `JsonTypeInfo` metadata. Generated examples are canonicalized and must use synthetic values.

Supported governance classifications are `Public`, `Internal`, `Personal`, `Confidential`, and `SecretProhibited`. Classification is documentation/review metadata; it does not configure encryption, broker retention, or authorization. `SecretProhibited` declarations fail generation, and committed/generated examples are scanned for obvious credential markers.

## Inbox, Outbox, Saga, and transports

`TCJ.EntityFrameworkCore` does not gain a dependency on `TCJ.Messaging` or `TCJ.Messaging.Contracts`. Integration validation belongs at application/test/startup boundaries where both systems are already present. The existing Outbox bridge continues to resolve the runtime `IMessageContractRegistry`; no second Outbox mechanism is introduced.

Saga state schema versions remain separate from message versions. When Saga messages cross a transport boundary, they use the same governed messaging identity. RabbitMQ, Azure Service Bus, and Kafka adapters continue to preserve message type/version and do not own schemas.

## Repository and downstream CI

Repository configuration is fail-closed:

```bash
python3 eng/verify-message-contracts.py validate-config

dotnet test tests/TCJ.Messaging.Contracts.Tests/TCJ.Messaging.Contracts.Tests.csproj \
  -c Release \
  --logger "trx;LogFileName=message-contracts.trx" \
  --results-directory TestResults/MessageContracts

dotnet run --project eng/TCJ.MessageContracts.Tool \
  -c Release -- verify \
  --output artifacts/message-contracts/generated
```

Applications can use the package and the same governance primitives in their own CI to generate a candidate artifact tree, compare it with an immutable release-derived baseline, reject unreviewed `ReviewRequired` findings, validate examples/upcasters, and publish the resulting manifest as release evidence.

No runtime network access, hosted schema registry, automatic semantic migration, or non-JSON schema format is required or claimed by this implementation.


Generated artifacts use the consumer-selected output root and never require a repository-root application contract catalog. Logical `MessageType` remains unchanged in metadata. When an otherwise valid runtime identity is not a portable filesystem segment (for example a Windows reserved device name or a trailing-dot segment), only the artifact directory segment is deterministically base64url-escaped with a `~` prefix; this avoids tightening the existing runtime message-type syntax.
