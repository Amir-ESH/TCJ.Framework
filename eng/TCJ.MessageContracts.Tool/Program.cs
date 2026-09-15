using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Messaging.Contracts;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Serialization;

return await ProgramEntry.RunAsync(args);

internal static class ProgramEntry
{
    public static Task<int> RunAsync(string[] args)
    {
        string command = args.FirstOrDefault() ?? "verify";
        string output = ReadOption(args, "--output") ?? Path.Combine("artifacts", "message-contracts", "tool");
        try
        {
            return Task.FromResult(command switch
            {
                "generate" => Generate(output),
                "verify" => Verify(output),
                _ => throw new ArgumentException("Expected 'generate' or 'verify'.")
            });
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Message-contract tooling failed: {exception.GetType().Name}: {exception.Message}");
            return Task.FromResult(1);
        }
    }

    private static int Generate(string output)
    {
        MessagingMessageContract contract = CreateContract();
        var definition = new MessageContractDefinition
        {
            RuntimeContract = contract,
            Metadata = new MessageContractMetadata
            {
                Owner = "TCJ.Framework",
                CompatibilityMode = MessageContractCompatibilityMode.Backward,
                ChangeSummary = "Synthetic repository tooling fixture.",
                SemanticCompatibilityNotes = "Synthetic fixture only; no application business contract is implied.",
                DataClassifications = [new("$.id", MessageDataClassification.Internal)]
            },
            Examples = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal)
            {
                ["valid-minimal"] = JsonSerializer.SerializeToUtf8Bytes(new ToolContract("fixture-1", null), ToolJsonContext.Default.ToolContract)
            }
        };
        new MessageContractArtifactGenerator().Generate(output, [definition]);
        Console.WriteLine(Path.GetFullPath(output));
        return 0;
    }

    private static int Verify(string output)
    {
        string runA = Path.Combine(output, "run-a");
        string runB = Path.Combine(output, "run-b");
        DeleteIfExists(runA);
        DeleteIfExists(runB);
        Generate(runA);
        Generate(runB);
        CompareTrees(runA, runB);

        ReadOnlyMemory<byte> previous = """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"],"additionalProperties":true}"""u8.ToArray();
        ReadOnlyMemory<byte> compatible = """{"type":"object","properties":{"id":{"type":"string"},"note":{"type":["string","null"]}},"required":["id"],"additionalProperties":true}"""u8.ToArray();
        ReadOnlyMemory<byte> breaking = """{"type":"object","properties":{"id":{"type":"string"},"note":{"type":"string"}},"required":["id","note"],"additionalProperties":true}"""u8.ToArray();
        var analyzer = new MessageContractCompatibilityAnalyzer();
        if (analyzer.Analyze(previous, compatible, MessageContractCompatibilityMode.Backward).Status != MessageContractCompatibilityStatus.Compatible ||
            analyzer.Analyze(previous, breaking, MessageContractCompatibilityMode.Backward).Status != MessageContractCompatibilityStatus.Breaking)
            throw new InvalidOperationException("Compatibility direction self-check failed.");

        Console.WriteLine("Message-contract deterministic generation and compatibility self-check passed.");
        return 0;
    }

    private static MessagingMessageContract CreateContract()
    {
        var services = new ServiceCollection();
        services.AddTcjMessaging();
        services.AddTcjMessage("tcj.fixture.message", 1, ToolJsonContext.Default.ToolContract);
        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IMessageContractRegistry>().Resolve("tcj.fixture.message", 1);
    }

    private static void CompareTrees(string first, string second)
    {
        string[] firstFiles = Directory.GetFiles(first, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(first, path)).Order(StringComparer.Ordinal).ToArray();
        string[] secondFiles = Directory.GetFiles(second, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(second, path)).Order(StringComparer.Ordinal).ToArray();
        if (!firstFiles.SequenceEqual(secondFiles, StringComparer.Ordinal))
            throw new InvalidOperationException("Repeated generation produced a different file set.");
        foreach (string relative in firstFiles)
        {
            if (!File.ReadAllBytes(Path.Combine(first, relative)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(second, relative))))
                throw new InvalidOperationException($"Repeated generation differs for '{relative}'.");
        }
    }

    private static string? ReadOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {name}.");
        return args[index + 1];
    }

    private static void DeleteIfExists(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}

internal sealed record ToolContract(string Id, string? Note);

[JsonSerializable(typeof(ToolContract))]
internal sealed partial class ToolJsonContext : JsonSerializerContext;
