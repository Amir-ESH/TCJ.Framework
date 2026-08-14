using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace TCJ.Concurrency.Tests.Infrastructure;

internal sealed class StressTrace
{
    public int SchemaVersion { get; init; } = 1;
    public required string Scenario { get; init; }
    public required string Group { get; init; }
    public required string Status { get; set; }
    public int Seed { get; init; }
    public int Workers { get; init; }
    public int Iterations { get; init; }
    public int OperationTimeoutMilliseconds { get; init; }
    public int ScenarioTimeoutSeconds { get; init; }
    public required string OperatingSystem { get; init; }
    public required string Architecture { get; init; }
    public required string Runtime { get; init; }
    public required string CommitSha { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset CompletedAtUtc { get; set; }
    public int ExpectedOperations { get; init; }
    public int CompletedOperations { get; set; }
    public int DuplicateOperations { get; set; }
    public int MissingOperations { get; set; }
    public int CanceledOperations { get; set; }
    public bool DeadlockDetected { get; set; }
    public bool TimeoutDetected { get; set; }
    public int ScopeLeakage { get; set; }
    public int IdentityLeakage { get; set; }
    public int TransactionInterference { get; set; }
    public List<StressException> Exceptions { get; } = [];
    public List<StressOperationRecord> Timeline { get; } = [];
    public required ReplayMetadata Replay { get; init; }
}

internal sealed record ReplayMetadata(
    string Scenario,
    int Seed,
    int Workers,
    int Iterations,
    string Command);

internal sealed record StressException(
    string OperationId,
    string ExceptionType,
    string Message,
    bool TimedOut);

internal sealed record StressOperationRecord(
    long Sequence,
    string OperationId,
    int Worker,
    int Iteration,
    string State,
    DateTimeOffset TimestampUtc);

internal enum StressViolationKind
{
    ScopeLeakage,
    IdentityLeakage,
    TransactionInterference
}
