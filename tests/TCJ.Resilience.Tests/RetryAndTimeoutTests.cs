using Microsoft.Extensions.Time.Testing;
using TCJ.Core.Resilience;
using TCJ.Resilience.Tests.Infrastructure;

namespace TCJ.Resilience.Tests;

public sealed class RetryAndTimeoutTests
{
    private static TransientFailureDetector CreateDetector() =>
        new([new InjectedTransientClassifier()]);

    [Fact]
    [Trait("Category", "Retry")]
    public async Task Retry_success_fails_twice_then_succeeds_without_duplicate_side_effect()
    {
        var injector = DeterministicFaultInjector.FailFirst(2);
        var options = new TcjRetryOptions { MaxRetryAttempts = 3, UseJitter = false, BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero };
        var policy = new TcjRetryPolicy(CreateDetector(), options);
        int committedSideEffects = 0;

        await policy.ExecuteAsync(async token =>
        {
            await injector.CheckpointAsync(token);
            Interlocked.Increment(ref committedSideEffects);
        }, "test_retry_success");

        Assert.Equal(3, injector.AttemptCount);
        Assert.Equal(1, committedSideEffects);
        ResilienceTrace.Write(nameof(Retry_success_fails_twice_then_succeeds_without_duplicate_side_effect), new { attempts = injector.AttemptCount, committedSideEffects, history = injector.History });
    }

    [Fact]
    [Trait("Category", "Retry")]
    public async Task Retry_exhaustion_respects_bound_and_preserves_last_exception()
    {
        var failures = new List<InjectedTransientException>();
        var injector = new DeterministicFaultInjector(
            [1, 2, 3, 4, 5],
            exceptionFactory: () =>
            {
                var failure = new InjectedTransientException($"attempt-{failures.Count + 1}");
                failures.Add(failure);
                return failure;
            });
        var options = new TcjRetryOptions { MaxRetryAttempts = 2, UseJitter = false, BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero };
        var policy = new TcjRetryPolicy(CreateDetector(), options);

        InjectedTransientException actual = await Assert.ThrowsAsync<InjectedTransientException>(
            () => policy.ExecuteAsync(injector.CheckpointAsync, "test_retry_exhaustion"));

        Assert.Equal(3, injector.AttemptCount);
        Assert.Equal(6, injector.History.Count);
        Assert.Equal(3, injector.History.Count(item => item.Outcome == "failed"));
        Assert.Same(failures[^1], actual);
        ResilienceTrace.Write(nameof(Retry_exhaustion_respects_bound_and_preserves_last_exception), new { attempts = injector.AttemptCount, finalType = actual.GetType().Name, history = injector.History });
    }

    [Fact]
    [Trait("Category", "Retry")]
    public async Task Retry_permanent_failure_is_not_retried()
    {
        var permanent = new InvalidOperationException("permanent");
        int attempts = 0;
        var policy = new TcjRetryPolicy(CreateDetector(), new TcjRetryOptions());

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            policy.ExecuteAsync<int>(_ =>
            {
                attempts++;
                throw permanent;
            }, "test_permanent"));

        Assert.Same(permanent, actual);
        Assert.Equal(1, attempts);
    }

    [Fact]
    [Trait("Category", "Retry")]
    public async Task Retry_zero_attempts_disables_retry()
    {
        int attempts = 0;
        var policy = new TcjRetryPolicy(
            CreateDetector(),
            new TcjRetryOptions { MaxRetryAttempts = 0 });

        await Assert.ThrowsAsync<InjectedTransientException>(() =>
            policy.ExecuteAsync<int>(_ =>
            {
                attempts++;
                throw new InjectedTransientException();
            }, "test_retry_disabled"));

        Assert.Equal(1, attempts);
    }

    [Fact]
    [Trait("Category", "Retry")]
    public void Retry_options_reject_negative_and_unbounded_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TcjRetryPolicy(CreateDetector(), new TcjRetryOptions { MaxRetryAttempts = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TcjRetryPolicy(CreateDetector(), new TcjRetryOptions { MaxRetryAttempts = 6 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TcjRetryPolicy(CreateDetector(), new TcjRetryOptions { MaxDelay = TimeSpan.FromSeconds(31) }));
    }

    [Fact]
    [Trait("Category", "Retry")]
    public void Retry_jitter_is_deterministic_in_tests_and_never_exceeds_max_delay()
    {
        var options = new TcjRetryOptions
        {
            MaxRetryAttempts = 5,
            BaseDelay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(3),
            UseJitter = true
        };
        var first = new TcjRetryPolicy(CreateDetector(), options, TimeProvider.System, new Random(42));
        var second = new TcjRetryPolicy(CreateDetector(), options, TimeProvider.System, new Random(42));

        TimeSpan[] schedule1 = Enumerable.Range(1, 5).Select(first.GetDelay).ToArray();
        TimeSpan[] schedule2 = Enumerable.Range(1, 5).Select(second.GetDelay).ToArray();

        Assert.Equal(schedule1, schedule2);
        Assert.All(schedule1, delay => Assert.InRange(delay, TimeSpan.Zero, options.MaxDelay));

        var deterministic = new TcjRetryPolicy(
            CreateDetector(),
            new TcjRetryOptions
            {
                MaxRetryAttempts = 5,
                BaseDelay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(3),
                UseJitter = false
            });
        Assert.Equal(
            [
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(3)
            ],
            Enumerable.Range(1, 5).Select(deterministic.GetDelay).ToArray());
    }

    [Fact]
    [Trait("Category", "Retry")]
    [Trait("Category", "Cancellation")]
    public async Task FaultInjection_selected_delay_and_cancellation_schedule_is_controllable_and_recorded()
    {
        var fakeTime = new FakeTimeProvider();
        using var cancellationSource = new CancellationTokenSource();
        var injector = new DeterministicFaultInjector(
            delayAttempts: [1],
            cancellationAttempts: [2],
            delay: TimeSpan.FromSeconds(5),
            timeProvider: fakeTime,
            cancellationSource: cancellationSource);

        Task first = injector.CheckpointAsync(cancellationSource.Token);
        fakeTime.Advance(TimeSpan.FromSeconds(5));
        await first;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            injector.CheckpointAsync(cancellationSource.Token));

        Assert.Equal(2, injector.AttemptCount);
        Assert.Contains(injector.History, item => item.Attempt == 1 && item.Outcome == "delayed");
        Assert.Contains(injector.History, item => item.Attempt == 2 && item.Outcome == "cancel-triggered");
    }

    [Fact]
    [Trait("Category", "Cancellation")]
    public async Task Cancellation_interrupts_retry_delay_immediately()
    {
        var fakeTime = new FakeTimeProvider();
        var policy = new TcjRetryPolicy(
            CreateDetector(),
            new TcjRetryOptions
            {
                MaxRetryAttempts = 3,
                BaseDelay = TimeSpan.FromSeconds(10),
                MaxDelay = TimeSpan.FromSeconds(10),
                UseJitter = false
            },
            fakeTime,
            new Random(1));
        using var cancellationSource = new CancellationTokenSource();
        int attempts = 0;

        Task operation = policy.ExecuteAsync<int>(_ =>
        {
            attempts++;
            throw new InjectedTransientException();
        }, "test_retry_cancel", cancellationSource.Token);
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(1, attempts);
    }

    [Fact]
    [Trait("Category", "Timeout")]
    public async Task Timeout_policy_timeout_is_distinct_from_caller_cancellation()
    {
        var fakeTime = new FakeTimeProvider();
        var policy = new TcjTimeoutPolicy(
            new TcjTimeoutOptions { OperationTimeout = TimeSpan.FromSeconds(5) },
            fakeTime);

        Task operation = policy.ExecuteAsync(
            token => Task.Delay(TimeSpan.FromMinutes(1), fakeTime, token),
            "test_timeout");
        fakeTime.Advance(TimeSpan.FromSeconds(5));

        TcjTimeoutException exception = await Assert.ThrowsAsync<TcjTimeoutException>(() => operation);
        Assert.Equal(TimeSpan.FromSeconds(5), exception.Timeout);
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        ResilienceTrace.Write(nameof(Timeout_policy_timeout_is_distinct_from_caller_cancellation), new { timeoutSeconds = exception.Timeout.TotalSeconds });
    }

    [Fact]
    [Trait("Category", "Cancellation")]
    public async Task Timeout_caller_cancellation_remains_operation_canceled()
    {
        var fakeTime = new FakeTimeProvider();
        var policy = new TcjTimeoutPolicy(
            new TcjTimeoutOptions { OperationTimeout = TimeSpan.FromSeconds(30) },
            fakeTime);
        using var cancellationSource = new CancellationTokenSource();

        Task operation = policy.ExecuteAsync(
            token => Task.Delay(TimeSpan.FromMinutes(1), fakeTime, token),
            "test_caller_cancel",
            cancellationSource.Token);
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    [Trait("Category", "Timeout")]
    public void Timeout_options_are_bounded()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TcjTimeoutPolicy(new TcjTimeoutOptions { OperationTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TcjTimeoutPolicy(new TcjTimeoutOptions { OperationTimeout = TimeSpan.FromSeconds(121) }));
    }

    [Fact]
    [Trait("Category", "Retry")]
    public void Retry_transient_detector_uses_db_signal_and_excludes_cancellation_and_argument_errors()
    {
        var detector = CreateDetector();
        var permissiveDetector = new TransientFailureDetector([new AlwaysTransientClassifier()]);

        Assert.True(detector.IsTransient(new TestDbException(true)));
        Assert.True(detector.IsTransient(new TimeoutException()));
        Assert.False(detector.IsTransient(new TestDbException(false)));
        Assert.False(detector.IsTransient(new OperationCanceledException()));
        Assert.False(detector.IsTransient(new ArgumentException("bad input")));
        Assert.False(permissiveDetector.IsTransient(new ArgumentException("still permanent")));
        Assert.False(permissiveDetector.IsTransient(new InvalidOperationException("configuration/coding failure")));
    }

    private sealed class AlwaysTransientClassifier : ITransientFailureClassifier
    {
        public bool IsTransient(Exception exception) => true;
    }
}
