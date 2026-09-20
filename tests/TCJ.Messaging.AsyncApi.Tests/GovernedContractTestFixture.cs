using System.Text;
using TCJ.Messaging.Contracts;

namespace TCJ.Messaging.AsyncApi.Tests;

internal sealed class GovernedContractTestFixture : IDisposable
{
    private readonly List<MessageContractArtifact> _contracts = [];

    public GovernedContractTestFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "tcj-asyncapi-step53-3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public MessageContractArtifact AddContract(
        string messageType,
        int messageVersion,
        string? schemaJson = null,
        bool includeExample = true,
        bool deprecated = false)
    {
        schemaJson ??= """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "properties": {
                "id": { "type": "string" }
              },
              "required": ["id"],
              "additionalProperties": false
            }
            """;

        byte[] canonical = MessageContractSchemaGenerator.Canonicalize(Encoding.UTF8.GetBytes(schemaJson));
        MessageContractFingerprint fingerprint = MessageContractSchemaGenerator.ComputeFingerprint(canonical);
        string contractDirectory = $"{messageType}/v{messageVersion}";
        string schemaPath = contractDirectory + "/schema.json";
        Write(schemaPath, canonical);

        string[] examples = [];
        if (includeExample)
        {
            string examplePath = contractDirectory + "/examples/valid-minimal.json";
            Write(examplePath, "{\"id\":\"fixture-1\"}"u8.ToArray());
            examples = [examplePath];
        }

        var artifact = new MessageContractArtifact
        {
            MessageType = messageType,
            MessageVersion = messageVersion,
            SchemaDraft = MessageContractSchemaGenerator.SchemaDraft,
            SchemaPath = schemaPath,
            WireSchemaFingerprint = fingerprint,
            Owner = "synthetic-test-owner",
            CompatibilityMode = MessageContractCompatibilityMode.Backward,
            Deprecated = deprecated,
            DeprecatedSince = deprecated ? messageVersion : null,
            ReplacementVersion = deprecated ? messageVersion + 1 : null,
            ChangeSummary = "Synthetic Step 52 fixture for AsyncAPI contract-resolution tests.",
            MigrationNotes = deprecated ? "Synthetic migration guidance." : null,
            SemanticCompatibilityNotes = "Synthetic reviewed compatibility evidence.",
            DataClassifications = [new("$.id", MessageDataClassification.Internal)],
            ReviewedExceptions = [new("SYNTHETIC-REVIEW", "Synthetic reviewed exception for test coverage.")],
            Examples = examples
        };
        _contracts.Add(artifact);
        return artifact;
    }

    public void WriteManifest(int schemaVersion = MessageContractArtifactGenerator.ManifestSchemaVersion)
    {
        var manifest = new MessageContractManifest
        {
            SchemaVersion = schemaVersion,
            CanonicalizationVersion = MessageContractSchemaGenerator.CanonicalizationVersion,
            SchemaDraft = MessageContractSchemaGenerator.SchemaDraft,
            FingerprintAlgorithm = MessageContractSchemaGenerator.FingerprintAlgorithm,
            Contracts = _contracts.ToArray()
        };
        Write("manifest.json", MessageContractManifestSerializer.Serialize(manifest));
    }

    public void WriteManifestContracts(params MessageContractArtifact[] contracts)
    {
        var manifest = new MessageContractManifest
        {
            SchemaVersion = MessageContractArtifactGenerator.ManifestSchemaVersion,
            CanonicalizationVersion = MessageContractSchemaGenerator.CanonicalizationVersion,
            SchemaDraft = MessageContractSchemaGenerator.SchemaDraft,
            FingerprintAlgorithm = MessageContractSchemaGenerator.FingerprintAlgorithm,
            Contracts = contracts
        };
        Write("manifest.json", MessageContractManifestSerializer.Serialize(manifest));
    }

    public void Write(string relativePath, ReadOnlySpan<byte> bytes)
    {
        string fullPath = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, bytes.ToArray());
    }

    public void WriteText(string relativePath, string value) => Write(relativePath, Encoding.UTF8.GetBytes(value));

    public byte[] Read(string relativePath) => File.ReadAllBytes(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}
