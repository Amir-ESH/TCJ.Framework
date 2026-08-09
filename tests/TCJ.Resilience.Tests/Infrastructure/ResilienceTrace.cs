using System.Text.Json;

namespace TCJ.Resilience.Tests.Infrastructure;

internal static class ResilienceTrace
{
    internal static void Write(string scenario, object payload)
    {
        string? directory = Environment.GetEnvironmentVariable("TCJ_RESILIENCE_TRACE_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        string safeName = string.Concat(scenario.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        string path = Path.Combine(directory, safeName + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(
            new
            {
                scenario,
                payload
            },
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
