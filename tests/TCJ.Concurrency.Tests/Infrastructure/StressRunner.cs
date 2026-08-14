using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TCJ.Concurrency.Tests.Infrastructure;

internal sealed class StressOperationContext
{
    private readonly StressRunState _state;

    internal StressOperationContext(
        StressRunState state,
        int worker,
        int iteration,
        int seed,
        CancellationToken cancellationToken)
    {
        _state = state;
        Worker = worker;
        Iteration = iteration;
        Seed = seed;
        CancellationToken = cancellationToken;
        OperationId = $"w{worker:D2}-i{iteration:D5}";
    }

    public int Worker { get; }
    public int Iteration { get; }
    public int Seed { get; }
    public string OperationId { get; }
    public CancellationToken CancellationToken { get; }

    public void ReportViolation(StressViolationKind kind) => _state.ReportViolation(kind);
}

internal static class StressRunner
{
    private const int MaximumTimelineEntries = 500;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task RunAsync(
        string scenario,
        string group,
        Func<StressOperationContext, Task> operation,
        Func<Task>? validate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario);
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentNullException.ThrowIfNull(operation);

        StressSettings settings = StressSettings.Load(group);
        Directory.CreateDirectory(settings.TraceDirectory);
        Directory.CreateDirectory(settings.FailureDirectory);

        var state = new StressRunState();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var trace = CreateTrace(scenario, group, settings, started);
        using var scenarioCancellation = new CancellationTokenSource();
        var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ready = new CountdownEvent(settings.Workers);

        Task[] workers = Enumerable.Range(0, settings.Workers)
            .Select(worker => RunWorkerAsync(worker, settings, state, operation, startGate.Task, ready, scenarioCancellation.Token))
            .ToArray();

        try
        {
            if (!ready.Wait(TimeSpan.FromSeconds(Math.Min(10, settings.ScenarioTimeoutSeconds))))
            {
                trace.DeadlockDetected = true;
                throw new TimeoutException("Workers did not reach the synchronized start barrier.");
            }

            startGate.SetResult(true);
            Task allWorkers = Task.WhenAll(workers);
            Task timeout = Task.Delay(TimeSpan.FromSeconds(settings.ScenarioTimeoutSeconds));
            Task completed = await Task.WhenAny(allWorkers, timeout).ConfigureAwait(false);
            if (completed != allWorkers)
            {
                trace.DeadlockDetected = true;
                trace.TimeoutDetected = true;
                scenarioCancellation.Cancel();
                throw new TimeoutException($"Scenario exceeded {settings.ScenarioTimeoutSeconds} seconds.");
            }

            await allWorkers.ConfigureAwait(false);
            if (validate is not null)
            {
                await validate().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            state.RecordScenarioException(exception);
        }
        finally
        {
            scenarioCancellation.Cancel();
            PopulateTrace(trace, state);
            trace.CompletedAtUtc = DateTimeOffset.UtcNow;
            trace.Status = HasFailure(trace) ? "Fail" : "Pass";
            await WriteTraceAsync(trace, settings).ConfigureAwait(false);
        }

        if (HasFailure(trace))
        {
            string detail = trace.Exceptions.Count == 0
                ? "stress invariant failure"
                : string.Join(" | ", trace.Exceptions.Take(3).Select(item => $"{item.ExceptionType}: {item.Message}"));
            throw new InvalidOperationException($"Concurrency stress scenario '{scenario}' failed: {detail}");
        }
    }

    private static async Task RunWorkerAsync(
        int worker,
        StressSettings settings,
        StressRunState state,
        Func<StressOperationContext, Task> operation,
        Task startGate,
        CountdownEvent ready,
        CancellationToken scenarioToken)
    {
        int workerSeed = unchecked(settings.Seed * 397 ^ (worker + 1) * 7919);
        var random = new Random(workerSeed);
        int[] order = Enumerable.Range(0, settings.Iterations).OrderBy(_ => random.Next()).ToArray();
        ready.Signal();
        await startGate.ConfigureAwait(false);

        foreach (int iteration in order)
        {
            if (scenarioToken.IsCancellationRequested)
            {
                break;
            }

            var context = new StressOperationContext(state, worker, iteration, workerSeed, scenarioToken);
            state.RecordStart(context);
            CancellationTokenSource? operationCancellation = null;
            try
            {
                if ((random.Next() & 1) == 0)
                {
                    await Task.Yield();
                }

                int perturbation = random.Next(0, 3);
                if (perturbation > 0)
                {
                    await Task.Delay(perturbation, scenarioToken).ConfigureAwait(false);
                }

                operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(scenarioToken);
                operationCancellation.CancelAfter(settings.OperationTimeoutMilliseconds);
                Task operationTask = operation(new StressOperationContext(state, worker, iteration, workerSeed, operationCancellation.Token));
                await operationTask.WaitAsync(TimeSpan.FromMilliseconds(settings.OperationTimeoutMilliseconds), scenarioToken).ConfigureAwait(false);
                state.RecordCompletion(context);
            }
            catch (OperationCanceledException exception) when (!scenarioToken.IsCancellationRequested && operationCancellation?.IsCancellationRequested == true)
            {
                state.RecordException(
                    context,
                    new TimeoutException($"Operation exceeded {settings.OperationTimeoutMilliseconds} milliseconds.", exception),
                    timedOut: true);
                break;
            }
            catch (OperationCanceledException exception) when (!scenarioToken.IsCancellationRequested)
            {
                state.RecordCancellation(context, exception);
                break;
            }
            catch (TimeoutException exception)
            {
                state.RecordException(context, exception, timedOut: true);
                break;
            }
            catch (Exception exception)
            {
                state.RecordException(context, exception, timedOut: false);
                break;
            }
            finally
            {
                operationCancellation?.Dispose();
            }
        }
    }

    private static StressTrace CreateTrace(string scenario, string group, StressSettings settings, DateTimeOffset started) =>
        new()
        {
            Scenario = scenario,
            Group = group,
            Status = "Running",
            Seed = settings.Seed,
            Workers = settings.Workers,
            Iterations = settings.Iterations,
            OperationTimeoutMilliseconds = settings.OperationTimeoutMilliseconds,
            ScenarioTimeoutSeconds = settings.ScenarioTimeoutSeconds,
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Runtime = RuntimeInformation.FrameworkDescription,
            CommitSha = settings.CommitSha,
            StartedAtUtc = started,
            ExpectedOperations = checked(settings.Workers * settings.Iterations),
            Replay = new ReplayMetadata(
                scenario,
                settings.Seed,
                settings.Workers,
                settings.Iterations,
                $"TCJ_STRESS_SEED={settings.Seed} TCJ_STRESS_WORKERS={settings.Workers} TCJ_STRESS_ITERATIONS={settings.Iterations} dotnet test tests/TCJ.Concurrency.Tests/TCJ.Concurrency.Tests.csproj -c Release --filter FullyQualifiedName~{scenario}")
        };

    private static void PopulateTrace(StressTrace trace, StressRunState state)
    {
        trace.CompletedOperations = state.OperationCounts.Count(pair => pair.Value > 0);
        trace.DuplicateOperations = state.OperationCounts.Count(pair => pair.Value > 1);
        trace.MissingOperations = Math.Max(0, trace.ExpectedOperations - trace.CompletedOperations);
        trace.CanceledOperations = state.CanceledOperations;
        trace.TimeoutDetected |= state.TimeoutDetected;
        trace.ScopeLeakage = state.ScopeLeakage;
        trace.IdentityLeakage = state.IdentityLeakage;
        trace.TransactionInterference = state.TransactionInterference;
        trace.Exceptions.AddRange(state.Exceptions);
        trace.Timeline.AddRange(state.Timeline.OrderBy(item => item.Sequence).Take(MaximumTimelineEntries));
    }

    private static bool HasFailure(StressTrace trace) =>
        trace.DeadlockDetected ||
        trace.TimeoutDetected ||
        trace.DuplicateOperations > 0 ||
        trace.MissingOperations > 0 ||
        trace.ScopeLeakage > 0 ||
        trace.IdentityLeakage > 0 ||
        trace.TransactionInterference > 0 ||
        trace.Exceptions.Count > 0;

    private static async Task WriteTraceAsync(StressTrace trace, StressSettings settings)
    {
        string safeScenario = string.Concat(trace.Scenario.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
        string fileName = $"{safeScenario}-{trace.Seed}.json";
        string tracePath = Path.Combine(settings.TraceDirectory, fileName);
        string json = JsonSerializer.Serialize(trace, JsonOptions);
        await File.WriteAllTextAsync(tracePath, json).ConfigureAwait(false);
        if (trace.Status == "Fail")
        {
            await File.WriteAllTextAsync(Path.Combine(settings.FailureDirectory, fileName), json).ConfigureAwait(false);
        }
    }
}

internal sealed class StressRunState
{
    private long _sequence;
    private int _canceledOperations;
    private int _timeoutDetected;
    private int _scopeLeakage;
    private int _identityLeakage;
    private int _transactionInterference;
    private readonly ConcurrentQueue<StressException> _exceptions = new();
    private readonly ConcurrentQueue<StressOperationRecord> _timeline = new();

    internal ConcurrentDictionary<string, int> OperationCounts { get; } = new(StringComparer.Ordinal);
    internal int CanceledOperations => Volatile.Read(ref _canceledOperations);
    internal bool TimeoutDetected => Volatile.Read(ref _timeoutDetected) != 0;
    internal int ScopeLeakage => Volatile.Read(ref _scopeLeakage);
    internal int IdentityLeakage => Volatile.Read(ref _identityLeakage);
    internal int TransactionInterference => Volatile.Read(ref _transactionInterference);
    internal IReadOnlyCollection<StressException> Exceptions => _exceptions.ToArray();
    internal IReadOnlyCollection<StressOperationRecord> Timeline => _timeline.ToArray();

    internal void RecordStart(StressOperationContext context) => RecordTimeline(context, "started");

    internal void RecordCompletion(StressOperationContext context)
    {
        OperationCounts.AddOrUpdate(context.OperationId, 1, static (_, count) => count + 1);
        RecordTimeline(context, "completed");
    }

    internal void RecordCancellation(StressOperationContext context, OperationCanceledException exception)
    {
        Interlocked.Increment(ref _canceledOperations);
        _exceptions.Enqueue(new StressException(context.OperationId, exception.GetType().FullName ?? exception.GetType().Name, "Operation canceled before successful completion.", false));
        RecordTimeline(context, "canceled");
    }

    internal void RecordException(StressOperationContext context, Exception exception, bool timedOut)
    {
        if (timedOut)
        {
            Interlocked.Exchange(ref _timeoutDetected, 1);
        }
        _exceptions.Enqueue(new StressException(context.OperationId, exception.GetType().FullName ?? exception.GetType().Name, Sanitize(exception.Message), timedOut));
        RecordTimeline(context, timedOut ? "timeout" : "failed");
    }

    internal void RecordScenarioException(Exception exception)
    {
        if (exception is TimeoutException)
        {
            Interlocked.Exchange(ref _timeoutDetected, 1);
        }
        _exceptions.Enqueue(new StressException("scenario", exception.GetType().FullName ?? exception.GetType().Name, Sanitize(exception.Message), exception is TimeoutException));
    }

    internal void ReportViolation(StressViolationKind kind)
    {
        switch (kind)
        {
            case StressViolationKind.ScopeLeakage:
                Interlocked.Increment(ref _scopeLeakage);
                break;
            case StressViolationKind.IdentityLeakage:
                Interlocked.Increment(ref _identityLeakage);
                break;
            case StressViolationKind.TransactionInterference:
                Interlocked.Increment(ref _transactionInterference);
                break;
        }
    }

    private void RecordTimeline(StressOperationContext context, string state) =>
        _timeline.Enqueue(new StressOperationRecord(
            Interlocked.Increment(ref _sequence),
            context.OperationId,
            context.Worker,
            context.Iteration,
            state,
            DateTimeOffset.UtcNow));

    private static string Sanitize(string value)
    {
        string normalized = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }
}
