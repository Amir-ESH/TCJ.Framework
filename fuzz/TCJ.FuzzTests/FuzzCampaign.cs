using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace TCJ.FuzzTests;

internal sealed class FuzzCampaign(
    IFuzzTarget target,
    string corpusDirectory,
    string outputDirectory,
    int seed,
    int durationSeconds,
    int maxInputBytes,
    int perInputTimeoutMilliseconds,
    long maximumProcessMemoryBytes)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<int> RunAsync()
    {
        Directory.CreateDirectory(outputDirectory);
        string failures = Path.Combine(outputDirectory, "failures");
        string minimized = Path.Combine(outputDirectory, "minimized");
        Directory.CreateDirectory(failures);
        Directory.CreateDirectory(minimized);

        byte[][] corpus = LoadCorpus();
        var random = new Random(seed);
        var stopwatch = Stopwatch.StartNew();
        long executions = 0;
        int crashes = 0, hangs = 0, unexpected = 0, invariants = 0, sizeViolations = 0, timeoutViolations = 0;
        string? failureKind = null, failureHash = null;
        int largestInputBytes = 0;
        long peakWorkingSetBytes = 0;
        using Process process = Process.GetCurrentProcess();

        while (stopwatch.Elapsed < TimeSpan.FromSeconds(durationSeconds))
        {
            byte[] input = Mutate(corpus[random.Next(corpus.Length)], random);
            largestInputBytes = Math.Max(largestInputBytes, input.Length);
            process.Refresh();
            peakWorkingSetBytes = Math.Max(peakWorkingSetBytes, process.WorkingSet64);
            if (peakWorkingSetBytes > maximumProcessMemoryBytes)
            {
                failureKind = FuzzFailureKind.ResourceExhaustion.ToString();
                failureHash = Persist(input, failures);
                sizeViolations++;
                break;
            }
            if (input.Length > maxInputBytes)
            {
                sizeViolations++;
                failureKind = FuzzFailureKind.ResourceExhaustion.ToString();
                failureHash = Persist(input, failures);
                break;
            }

            executions++;
            (FuzzFailureKind? kind, Exception? error) = await ExecuteWithTimeout(input);
            if (kind is null) continue;

            failureKind = kind.Value.ToString();
            switch (kind)
            {
                case FuzzFailureKind.Hang: hangs++; timeoutViolations++; break;
                case FuzzFailureKind.InvariantViolation: invariants++; break;
                case FuzzFailureKind.UnexpectedException: unexpected++; break;
                default: crashes++; break;
            }

            failureHash = Persist(input, failures);
            byte[] minimizedInput = await Minimize(input, kind.Value);
            Persist(minimizedInput, minimized);
            WriteFailureMetadata(kind.Value, error, failureHash, input.Length, minimizedInput.Length);
            break;
        }

        stopwatch.Stop();
        int unresolved = crashes + hangs + unexpected + invariants + sizeViolations;
        var result = new FuzzRunResult(target.Name, unresolved == 0 ? "Pass" : "Fail", seed,
            stopwatch.Elapsed.TotalSeconds, executions, crashes, hangs, unexpected, invariants,
            sizeViolations, timeoutViolations, largestInputBytes, peakWorkingSetBytes, unresolved == 0 ? 0 : 1, unresolved, failureKind, failureHash);
        File.WriteAllText(Path.Combine(outputDirectory, "result.json"), JsonSerializer.Serialize(result, JsonOptions));
        return unresolved == 0 ? 0 : 1;
    }

    private byte[][] LoadCorpus()
    {
        string[] files = Directory.Exists(corpusDirectory)
            ? Directory.GetFiles(corpusDirectory, "*", SearchOption.TopDirectoryOnly)
            : [];
        var entries = files.Select(File.ReadAllBytes).Where(data => data.Length <= maxInputBytes).ToList();
        if (entries.Count == 0) entries.Add([]);
        return entries.ToArray();
    }

    private byte[] Mutate(byte[] seedInput, Random random)
    {
        int maxLength = Math.Min(maxInputBytes, Math.Max(64, seedInput.Length + 64));
        int length = seedInput.Length == 0 ? random.Next(0, 33) : Math.Min(seedInput.Length, maxLength);
        byte[] data = seedInput.Take(length).ToArray();
        if (data.Length == 0) data = new byte[random.Next(0, 33)];
        int mutations = 1 + random.Next(8);
        for (int i = 0; i < mutations; i++)
        {
            if (data.Length == 0) break;
            data[random.Next(data.Length)] = (byte)random.Next(256);
        }
        return data;
    }

    private async Task<(FuzzFailureKind? Kind, Exception? Error)> ExecuteWithTimeout(byte[] input)
    {
        Task task = Task.Run(() => target.Execute(input));
        Task completed = await Task.WhenAny(task, Task.Delay(perInputTimeoutMilliseconds));
        if (!ReferenceEquals(task, completed)) return (FuzzFailureKind.Hang, null);
        try { await task; return (null, null); }
        catch (FuzzInvariantException ex) { return (FuzzFailureKind.InvariantViolation, ex); }
        catch (ArgumentException ex) { return (FuzzFailureKind.UnexpectedException, ex); }
        catch (Exception ex) { return (FuzzFailureKind.UnexpectedException, ex); }
    }

    private async Task<byte[]> Minimize(byte[] original, FuzzFailureKind expected)
    {
        byte[] current = original;
        while (current.Length > 1)
        {
            byte[] candidate = current[..Math.Max(1, current.Length / 2)];
            (FuzzFailureKind? kind, _) = await ExecuteWithTimeout(candidate);
            if (kind == expected) current = candidate; else break;
        }
        return current;
    }

    private static string Persist(byte[] input, string directory)
    {
        string hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(directory, $"{hash}.bin"), input);
        return hash;
    }

    private void WriteFailureMetadata(FuzzFailureKind kind, Exception? error, string hash, int originalBytes, int minimizedBytes)
    {
        var safe = new
        {
            target = target.Name,
            classification = kind.ToString(),
            inputSha256 = hash,
            originalBytes,
            minimizedBytes,
            exceptionType = error?.GetType().FullName,
            message = Sanitize(error?.Message),
            replayCommand = $"dotnet run --project fuzz/TCJ.FuzzTests/TCJ.FuzzTests.csproj -c Release -- --replay {target.Name} artifacts/fuzzing/fuzz-results/{target.Name}/failures/{hash}.bin"
        };
        File.WriteAllText(Path.Combine(outputDirectory, "failure.json"), JsonSerializer.Serialize(safe, JsonOptions));
    }

    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        string sanitized = value.Length > 512 ? value[..512] : value;
        foreach (string marker in new[] { "github_pat_", "ghp_", "BEGIN PRIVATE KEY", "AKIA" })
            sanitized = sanitized.Replace(marker, "<redacted>", StringComparison.OrdinalIgnoreCase);
        return sanitized;
    }
}
