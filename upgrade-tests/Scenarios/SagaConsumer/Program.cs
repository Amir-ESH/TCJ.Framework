using System.Text.Json;
using TCJ.Messaging.Sagas;
using TCJ.Messaging.Sagas.Configuration;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Extensions;
using TCJ.Messaging.Sagas.EntityFrameworkCore.SqlServer.Extensions;

var options = new TcjSagaOptions();
options.Validate();
SagaCorrelationKey correlation = SagaCorrelationKey.From("Case-Sensitive-Key");

var behavior = new
{
    schemaVersion = 1,
    scenario = "SagaConsumer",
    checks = new
    {
        boundedStatePayload = options.MaximumStatePayloadBytes == 256 * 1024,
        boundedTimerRetries = options.MaxTimerAttempts == 5,
        boundedCompensationRetries = options.MaxCompensationAttempts == 5,
        correlationIsRedacted = correlation.ToString() == "[redacted-correlation]",
        explicitStatusContract = Enum.GetNames<SagaStatus>().SequenceEqual(new[] { "Active", "Completed", "Failed", "Compensating", "Compensated" }),
        efIntegrationLoads = typeof(SagaModelBuilderExtensions).Assembly.GetName().Name == "TCJ.Messaging.Sagas.EntityFrameworkCore",
        sqlServerIntegrationLoads = typeof(SqlServerSagaModelBuilderExtensions).Assembly.GetName().Name == "TCJ.Messaging.Sagas.EntityFrameworkCore.SqlServer",
    },
};

await WriteBehaviorAsync(behavior);
Console.WriteLine("TCJ durable Saga target-only upgrade scenario passed");

static async Task WriteBehaviorAsync<T>(T value)
{
    string path = Environment.GetEnvironmentVariable("TCJ_UPGRADE_BEHAVIOR_PATH")
        ?? throw new InvalidOperationException("TCJ_UPGRADE_BEHAVIOR_PATH is required.");
    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Behavior path has no directory."));
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}
