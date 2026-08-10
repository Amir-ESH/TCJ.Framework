using System.Diagnostics;
using TCJ.Core.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.DomainEvents;
using TCJ.Core.Resilience;

namespace TCJ.DependencyInjection.DomainEvents;

/// <summary>
/// Provides a non-generic dispatch boundary for a runtime domain-event type.
/// </summary>
internal interface IDomainEventHandlerInvoker
{
    Task InvokeAsync(
        IDomainEvent domainEvent,
        CancellationToken cancellationToken);
}

/// <summary>
/// Invokes all handlers registered for <typeparamref name="TEvent"/> in
/// dependency-registration order.
/// </summary>
/// <typeparam name="TEvent">The concrete domain-event type.</typeparam>
internal sealed class DomainEventHandlerInvoker<TEvent> : IDomainEventHandlerInvoker
    where TEvent : IDomainEvent
{
    private readonly IEnumerable<IDomainEventHandler<TEvent>> _handlers;
    private readonly TcjRetryPolicy? _handlerRetryPolicy;
    private readonly TcjDomainEventResilienceOptions? _resilienceOptions;

    public DomainEventHandlerInvoker(IEnumerable<IDomainEventHandler<TEvent>> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = handlers;
    }

    public DomainEventHandlerInvoker(
        IEnumerable<IDomainEventHandler<TEvent>> handlers,
        IServiceProvider serviceProvider)
        : this(handlers)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _resilienceOptions = serviceProvider.GetService<TcjDomainEventResilienceOptions>();

        if (_resilienceOptions?.RetryTransientHandlerFailures == true)
        {
            ITransientFailureDetector detector = serviceProvider.GetRequiredService<ITransientFailureDetector>();
            TimeProvider timeProvider = serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
            _handlerRetryPolicy = new TcjRetryPolicy(detector, _resilienceOptions.Retry, timeProvider);
        }
    }

    public async Task InvokeAsync(
        IDomainEvent domainEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent is not TEvent typedEvent)
        {
            throw new ArgumentException(
                $"Domain event type '{domainEvent.GetType().FullName}' cannot be handled " +
                $"by an invoker for '{typeof(TEvent).FullName}'.",
                nameof(domainEvent));
        }

        Activity? activity = TcjTelemetry.StartActivity(
            CoreTelemetryDiagnostics.ActivitySource,
            TcjDiagnosticNames.Activities.DomainEventDispatch,
            TcjDiagnosticNames.Sources.Core,
            CoreTelemetryDiagnostics.PackageVersion,
            "dispatch");

        if (activity is not null)
        {
            activity.SetTag(
                TcjDiagnosticNames.Tags.DomainEventType,
                TcjTelemetry.NormalizeTypeName(typeof(TEvent)));

            if (_handlers.TryGetNonEnumeratedCount(out int handlerCount))
            {
                activity.SetTag(TcjDiagnosticNames.Tags.HandlerCount, handlerCount);
            }
        }

        bool measureDuration = TcjTelemetry.MetricsEnabled &&
            CoreTelemetryDiagnostics.DomainEventDispatchDuration.Enabled;
        long startedAt = measureDuration ? Stopwatch.GetTimestamp() : 0;
        string outcome = TcjDiagnosticNames.Outcomes.Success;

        try
        {
            foreach (IDomainEventHandler<TEvent> handler in _handlers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await InvokeHandlerAsync(handler, typedEvent, cancellationToken).ConfigureAwait(false);
            }

            TcjTelemetry.CompleteSuccess(activity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = TcjDiagnosticNames.Outcomes.Canceled;
            TcjTelemetry.CompleteCanceled(activity);
            throw;
        }
        catch (Exception exception)
        {
            outcome = TcjDiagnosticNames.Outcomes.Failure;
            TcjTelemetry.CompleteFailure(activity, exception);
            throw;
        }
        finally
        {
            RecordDispatchMetrics(outcome, startedAt, measureDuration);
            activity?.Dispose();
        }
    }

    private async Task InvokeHandlerAsync(
        IDomainEventHandler<TEvent> handler,
        TEvent domainEvent,
        CancellationToken cancellationToken)
    {
        Activity? activity = TcjTelemetry.StartActivity(
            CoreTelemetryDiagnostics.ActivitySource,
            TcjDiagnosticNames.Activities.DomainEventHandle,
            TcjDiagnosticNames.Sources.Core,
            CoreTelemetryDiagnostics.PackageVersion,
            "handle");

        if (activity is not null)
        {
            activity.SetTag(
                TcjDiagnosticNames.Tags.DomainEventType,
                TcjTelemetry.NormalizeTypeName(typeof(TEvent)));

            if (TcjTelemetry.RecordHandlerTypeNames)
            {
                activity.SetTag(
                    TcjDiagnosticNames.Tags.HandlerType,
                    TcjTelemetry.NormalizeTypeName(handler.GetType()));
            }
        }

        bool measureDuration = TcjTelemetry.MetricsEnabled &&
            CoreTelemetryDiagnostics.DomainEventHandlerDuration.Enabled;
        long startedAt = measureDuration ? Stopwatch.GetTimestamp() : 0;
        string outcome = TcjDiagnosticNames.Outcomes.Success;

        try
        {
            if (_handlerRetryPolicy is not null && _resilienceOptions?.RetryTransientHandlerFailures == true)
            {
                await _handlerRetryPolicy.ExecuteAsync(
                    token => handler.HandleAsync(domainEvent, token),
                    strategy: "domain_event_handler",
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await handler.HandleAsync(domainEvent, cancellationToken).ConfigureAwait(false);
            }

            TcjTelemetry.CompleteSuccess(activity);

            if (TcjTelemetry.MetricsEnabled && CoreTelemetryDiagnostics.DomainEventHandlersCompleted.Enabled)
            {
                TagList tags = CreateOutcomeTags(TcjDiagnosticNames.Outcomes.Success);
                CoreTelemetryDiagnostics.DomainEventHandlersCompleted.Add(1, tags);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = TcjDiagnosticNames.Outcomes.Canceled;
            TcjTelemetry.CompleteCanceled(activity);
            throw;
        }
        catch (Exception exception)
        {
            outcome = TcjDiagnosticNames.Outcomes.Failure;
            TcjTelemetry.CompleteFailure(activity, exception);

            if (TcjTelemetry.MetricsEnabled && CoreTelemetryDiagnostics.DomainEventHandlersFailed.Enabled)
            {
                TagList tags = CreateOutcomeTags(TcjDiagnosticNames.Outcomes.Failure);
                CoreTelemetryDiagnostics.DomainEventHandlersFailed.Add(1, tags);
            }

            throw;
        }
        finally
        {
            if (measureDuration)
            {
                TagList tags = CreateOutcomeTags(outcome);
                CoreTelemetryDiagnostics.DomainEventHandlerDuration.Record(
                    Stopwatch.GetElapsedTime(startedAt).TotalSeconds,
                    tags);
            }

            activity?.Dispose();
        }
    }

    private static void RecordDispatchMetrics(string outcome, long startedAt, bool measureDuration)
    {
        if (!TcjTelemetry.MetricsEnabled)
        {
            return;
        }

        TagList tags = CreateOutcomeTags(outcome);

        if (CoreTelemetryDiagnostics.DomainEventsDispatched.Enabled)
        {
            CoreTelemetryDiagnostics.DomainEventsDispatched.Add(1, tags);
        }

        if (measureDuration)
        {
            CoreTelemetryDiagnostics.DomainEventDispatchDuration.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalSeconds,
                tags);
        }
    }

    private static TagList CreateOutcomeTags(string outcome) =>
        new()
        {
            { TcjDiagnosticNames.Tags.OperationOutcome, outcome }
        };
}
