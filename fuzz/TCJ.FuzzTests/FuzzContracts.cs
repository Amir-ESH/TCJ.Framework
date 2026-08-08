namespace TCJ.FuzzTests;

internal enum FuzzFailureKind
{
    Crash,
    Hang,
    UnexpectedException,
    InvariantViolation,
    ResourceExhaustion,
    PerformanceAnomaly,
    ExpectedValidationFailure,
    ToolingFailure
}

internal interface IFuzzTarget
{
    string Name { get; }
    void Execute(ReadOnlyMemory<byte> input);
}

internal sealed record FuzzRunResult(
    string Target,
    string Status,
    int Seed,
    double DurationSeconds,
    long Executions,
    int Crashes,
    int Hangs,
    int UnexpectedExceptions,
    int InvariantViolations,
    int InputSizeViolations,
    int TimeoutViolations,
    int LargestInputBytes,
    long PeakWorkingSetBytes,
    int MinimizedFailures,
    int UnresolvedFailures,
    string? FailureKind,
    string? FailureHash);

internal sealed class FuzzInvariantException(string message) : Exception(message);
