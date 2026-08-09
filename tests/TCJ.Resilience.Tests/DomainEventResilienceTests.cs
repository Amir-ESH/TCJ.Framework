using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.DomainEvents;
using TCJ.Core.Resilience;
using TCJ.DependencyInjection.DomainEvents;
using TCJ.DependencyInjection.Extensions;
using TCJ.Resilience.Tests.Infrastructure;

namespace TCJ.Resilience.Tests;

public sealed class DomainEventResilienceTests
{
    [Fact]
    [Trait("Category", "DomainEvents")]
    public void DomainEvents_resilience_registration_is_idempotent_and_preserves_first_configuration()
    {
        var services = new ServiceCollection();
        services.AddTcjDomainEventResilience(options =>
        {
            options.RetryTransientHandlerFailures = true;
            options.Retry.MaxRetryAttempts = 1;
        });
        services.AddTcjDomainEventResilience(options =>
        {
            options.RetryTransientHandlerFailures = false;
            options.Retry.MaxRetryAttempts = 2;
        });

        using ServiceProvider provider = services.BuildServiceProvider();
        TcjDomainEventResilienceOptions options =
            provider.GetRequiredService<TcjDomainEventResilienceOptions>();

        Assert.True(options.RetryTransientHandlerFailures);
        Assert.Equal(1, options.Retry.MaxRetryAttempts);
    }

    [Fact]
    [Trait("Category", "DomainEvents")]
    public async Task DomainEvents_default_behavior_does_not_retry_transient_handler_failure()
    {
        var handler = new FaultingHandler(failuresBeforeSuccess: 1, transient: true);
        using ServiceProvider services = CreateServices(handler, enableRetry: false);
        IDomainEventDispatcher dispatcher = services.GetRequiredService<IDomainEventDispatcher>();

        await Assert.ThrowsAsync<InjectedTransientException>(() =>
            dispatcher.DispatchAsync([new TestEvent()]));

        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    [Trait("Category", "DomainEvents")]
    public async Task DomainEvents_opt_in_retries_only_failing_handler_and_preserves_order()
    {
        var successfulBefore = new RecordingHandler("before");
        var transient = new FaultingHandler(failuresBeforeSuccess: 2, transient: true);
        var successfulAfter = new RecordingHandler("after");
        using ServiceProvider services = CreateServices(
            successfulBefore,
            transient,
            successfulAfter,
            enableRetry: true);
        IDomainEventDispatcher dispatcher = services.GetRequiredService<IDomainEventDispatcher>();

        await dispatcher.DispatchAsync([new TestEvent()]);

        Assert.Equal(1, successfulBefore.Attempts);
        Assert.Equal(3, transient.Attempts);
        Assert.Equal(1, successfulAfter.Attempts);
        ResilienceTrace.Write(nameof(DomainEvents_opt_in_retries_only_failing_handler_and_preserves_order), new
        {
            before = successfulBefore.Attempts,
            transient = transient.Attempts,
            after = successfulAfter.Attempts
        });
    }

    [Fact]
    [Trait("Category", "DomainEvents")]
    public async Task DomainEvents_permanent_handler_failure_is_not_retried_and_later_handler_is_not_invoked()
    {
        var permanent = new FaultingHandler(failuresBeforeSuccess: 1, transient: false);
        var later = new RecordingHandler("later");
        using ServiceProvider services = CreateServices(permanent, later, enableRetry: true);
        IDomainEventDispatcher dispatcher = services.GetRequiredService<IDomainEventDispatcher>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.DispatchAsync([new TestEvent()]));

        Assert.Equal(1, permanent.Attempts);
        Assert.Equal(0, later.Attempts);
    }

    [Fact]
    [Trait("Category", "DomainEvents")]
    [Trait("Category", "Cancellation")]
    public async Task DomainEvents_cancellation_stops_handler_retries()
    {
        using var cancellationSource = new CancellationTokenSource();
        var handler = new CancelingTransientHandler(cancellationSource);
        using ServiceProvider services = CreateServices(handler, enableRetry: true);
        IDomainEventDispatcher dispatcher = services.GetRequiredService<IDomainEventDispatcher>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dispatcher.DispatchAsync([new TestEvent()], cancellationSource.Token));

        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    [Trait("Category", "Idempotency")]
    public async Task Idempotency_duplicate_side_effect_guard_commits_once_across_retry_attempts()
    {
        var processed = new HashSet<Guid>();
        var gate = new object();
        Guid operationId = Guid.NewGuid();
        var injector = DeterministicFaultInjector.FailFirst(2);
        var policy = new TcjRetryPolicy(
            new TransientFailureDetector([new InjectedTransientClassifier()]),
            new TcjRetryOptions
            {
                MaxRetryAttempts = 2,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                UseJitter = false
            });
        int durableEffects = 0;

        await policy.ExecuteAsync(async token =>
        {
            // Simulate a durable side effect that succeeded before the caller
            // observed a transient failure. The consumer-owned operation key
            // prevents the retry from committing it again.
            lock (gate)
            {
                if (processed.Add(operationId))
                {
                    durableEffects++;
                }
            }

            await injector.CheckpointAsync(token);
        }, "idempotent_side_effect");

        Assert.Equal(3, injector.AttemptCount);
        Assert.Equal(1, durableEffects);
    }

    private static ServiceProvider CreateServices(
        IDomainEventHandler<TestEvent> first,
        bool enableRetry) =>
        CreateServices([first], enableRetry);

    private static ServiceProvider CreateServices(
        IDomainEventHandler<TestEvent> first,
        IDomainEventHandler<TestEvent> second,
        bool enableRetry) =>
        CreateServices([first, second], enableRetry);

    private static ServiceProvider CreateServices(
        IDomainEventHandler<TestEvent> first,
        IDomainEventHandler<TestEvent> second,
        IDomainEventHandler<TestEvent> third,
        bool enableRetry) =>
        CreateServices([first, second, third], enableRetry);

    private static ServiceProvider CreateServices(
        IReadOnlyCollection<IDomainEventHandler<TestEvent>> handlers,
        bool enableRetry)
    {
        var services = new ServiceCollection();
        services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
        foreach (IDomainEventHandler<TestEvent> handler in handlers)
        {
            services.AddSingleton(typeof(IDomainEventHandler<TestEvent>), handler);
        }

        if (enableRetry)
        {
            services.AddSingleton<ITransientFailureClassifier, InjectedTransientClassifier>();
            services.AddTcjDomainEventResilience(options =>
            {
                options.RetryTransientHandlerFailures = true;
                options.Retry.MaxRetryAttempts = 2;
                options.Retry.BaseDelay = TimeSpan.Zero;
                options.Retry.MaxDelay = TimeSpan.Zero;
                options.Retry.UseJitter = false;
            });
        }

        return services.BuildServiceProvider();
    }

    private sealed record TestEvent : IDomainEvent
    {
        public DateTimeOffset OccurredOn { get; } = DateTimeOffset.UtcNow;
    }

    private sealed class RecordingHandler(string name) : IDomainEventHandler<TestEvent>
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);
        public Task HandleAsync(TestEvent domainEvent, CancellationToken cancellationToken)
        {
            _ = name;
            Interlocked.Increment(ref _attempts);
            return Task.CompletedTask;
        }
    }

    private sealed class FaultingHandler(int failuresBeforeSuccess, bool transient) : IDomainEventHandler<TestEvent>
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);

        public Task HandleAsync(TestEvent domainEvent, CancellationToken cancellationToken)
        {
            int attempt = Interlocked.Increment(ref _attempts);
            if (attempt <= failuresBeforeSuccess)
            {
                if (transient)
                {
                    throw new InjectedTransientException();
                }

                throw new InvalidOperationException("permanent handler failure");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class CancelingTransientHandler(CancellationTokenSource source) : IDomainEventHandler<TestEvent>
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);

        public Task HandleAsync(TestEvent domainEvent, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InjectedTransientException();
        }
    }
}
