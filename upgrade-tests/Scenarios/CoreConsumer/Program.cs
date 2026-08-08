using System.Text.Json;
using TCJ.Core.Extensions;
using TCJ.Core.Guards;
using TCJ.Core.Identifiers;
using TCJ.Core.Results;

Result<int> success = Result.Success(42);
Result<int> failure = Result.Failure<int>(CommonErrors.Validation("invalid input"));
Guid guid = new GuidGenerator(TimeProvider.System).CreateVersion7();

var behavior = new
{
    schemaVersion = 1,
    scenario = "CoreConsumer",
    checks = new
    {
        resultSuccess = success.IsSuccess && success.Value == 42,
        resultFailure = failure.IsFailure && failure.Errors.Count == 1,
        guard = Check.NotNullOrWhiteSpace("TCJ") == "TCJ",
        stringExtension = "consumer".EnsureEndsWith('!') == "consumer!",
        weekend = DayOfWeek.Saturday.IsWeekend(),
        guidAvailable = guid != Guid.Empty,
        timeProviderAvailable = TimeProvider.System.GetUtcNow() != default,
    },
};

await WriteBehaviorAsync(behavior);
Console.WriteLine("TCJ.Core upgrade scenario passed");

static async Task WriteBehaviorAsync<T>(T value)
{
    string path = Environment.GetEnvironmentVariable("TCJ_UPGRADE_BEHAVIOR_PATH")
        ?? throw new InvalidOperationException("TCJ_UPGRADE_BEHAVIOR_PATH is required.");
    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Behavior path has no directory."));
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}
