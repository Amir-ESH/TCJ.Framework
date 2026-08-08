using SharpFuzz;
using TCJ.FuzzTests;

if (args.Length == 0)
{
    Console.Error.WriteLine("Specify --managed, --sharpfuzz, or --replay.");
    return 2;
}

if (args[0] == "--sharpfuzz" && args.Length >= 2)
{
    IFuzzTarget target = FuzzTargetCatalog.Create(args[1]);
    Fuzzer.OutOfProcess.Run((Stream stream) =>
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        if (buffer.Length > 1_048_576) throw new InvalidDataException("Fuzz input exceeds the configured limit.");
        target.Execute(buffer.ToArray());
    });
    return 0;
}

if (args[0] == "--replay" && args.Length >= 3)
{
    IFuzzTarget target = FuzzTargetCatalog.Create(args[1]);
    byte[] input = File.ReadAllBytes(args[2]);
    if (input.Length > 1_048_576) throw new InvalidDataException("Replay input exceeds the configured limit.");
    target.Execute(input);
    return 0;
}

if (args[0] == "--managed")
{
    var values = Parse(args.Skip(1).ToArray());
    string targetName = Required(values, "target");
    string corpus = Required(values, "corpus");
    string output = Required(values, "output");
    int duration = Int(values, "duration", 30);
    int seed = Int(values, "seed", 39039);
    int maxInput = Int(values, "max-input-bytes", 1_048_576);
    int timeout = Int(values, "timeout-ms", 1_000);
    long maxMemory = Long(values, "max-memory-bytes", 536_870_912);
    return await new FuzzCampaign(FuzzTargetCatalog.Create(targetName), corpus, output, seed, duration, maxInput, timeout, maxMemory).RunAsync();
}

Console.Error.WriteLine("Invalid fuzz command line.");
return 2;

static Dictionary<string, string> Parse(string[] tokens)
{
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 0; i < tokens.Length; i += 2)
    {
        if (i + 1 >= tokens.Length || !tokens[i].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Arguments must be --name value pairs.");
        values[tokens[i][2..]] = tokens[i + 1];
    }
    return values;
}
static string Required(Dictionary<string, string> values, string name) =>
    values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"Missing --{name}.");
static int Int(Dictionary<string, string> values, string name, int fallback) =>
    values.TryGetValue(name, out string? value) ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : fallback;
static long Long(Dictionary<string, string> values, string name, long fallback) =>
    values.TryGetValue(name, out string? value) ? long.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : fallback;
