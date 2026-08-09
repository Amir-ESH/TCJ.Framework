using System.Diagnostics;
using TCJ.Core.Diagnostics;
using TCJ.Core.DomainEvents;
using TCJ.DependencyInjection.DomainEvents;

namespace TCJ.Observability.Tests;

public sealed class DomainEventTelemetryTests : IDisposable
{
    private const string PasswordMarker = "TCJ_TEST_PASSWORD_MARKER";
    private const string TokenMarker = "TCJ_TEST_TOKEN_MARKER";
    private const string ConnectionStringMarker = "TCJ_TEST_CONNECTION_STRING_MARKER";

    public DomainEventTelemetryTests() => TcjTelemetry.ResetForTests();

    [Fact]
    public async Task Dispatch_and_handlers_preserve_parenting_and_do_not_leak_payloads()
    {
        using var collector = new ActivityCollector(TcjDiagnosticNames.Sources.Core);
        using var request = new Activity("test.request").Start();
        var handlers = new IDomainEventHandler<TestEvent>[]
        {
            new SuccessHandler(),
            new SuccessHandler()
        };
        var invoker = new DomainEventHandlerInvoker<TestEvent>(handlers);
        var domainEvent = new TestEvent(
            DateTimeOffset.UtcNow,
            $"{PasswordMarker}:{TokenMarker}:{ConnectionStringMarker}");

        await invoker.InvokeAsync(domainEvent, CancellationToken.None);

        Activity dispatch = Assert.Single(
            collector.Activities,
            activity => activity.OperationName == TcjDiagnosticNames.Activities.DomainEventDispatch);
        Activity[] handlerActivities = collector.Activities
            .Where(activity => activity.OperationName == TcjDiagnosticNames.Activities.DomainEventHandle)
            .ToArray();

        Assert.Equal(2, handlerActivities.Length);
        Assert.Equal(request.TraceId, dispatch.TraceId);
        Assert.Equal(request.SpanId, dispatch.ParentSpanId);
        Assert.All(handlerActivities, handler =>
        {
            Assert.Equal(dispatch.TraceId, handler.TraceId);
            Assert.Equal(dispatch.SpanId, handler.ParentSpanId);
            Assert.Equal(ActivityStatusCode.Ok, handler.Status);
        });
        Assert.Equal(ActivityStatusCode.Ok, dispatch.Status);

        string emitted = string.Join(
            '\n',
            collector.Activities.SelectMany(static activity => activity.TagObjects)
                .Select(static tag => $"{tag.Key}={tag.Value}"));

        Assert.DoesNotContain(PasswordMarker, emitted, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenMarker, emitted, StringComparison.Ordinal);
        Assert.DoesNotContain(ConnectionStringMarker, emitted, StringComparison.Ordinal);
        Assert.DoesNotContain("raw_sql", emitted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connection_string", emitted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("entity.id", emitted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Handler_failure_records_type_without_message_and_preserves_exception()
    {
        using var collector = new ActivityCollector(TcjDiagnosticNames.Sources.Core);
        var expected = new InvalidOperationException($"failure {TokenMarker}");
        var invoker = new DomainEventHandlerInvoker<TestEvent>(
            new IDomainEventHandler<TestEvent>[] { new FailingHandler(expected) });

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => invoker.InvokeAsync(new TestEvent(DateTimeOffset.UtcNow, PasswordMarker), CancellationToken.None));

        Assert.Same(expected, actual);
        Activity dispatch = Assert.Single(
            collector.Activities,
            activity => activity.OperationName == TcjDiagnosticNames.Activities.DomainEventDispatch);
        Activity handler = Assert.Single(
            collector.Activities,
            activity => activity.OperationName == TcjDiagnosticNames.Activities.DomainEventHandle);

        Assert.Equal(ActivityStatusCode.Error, dispatch.Status);
        Assert.Equal(ActivityStatusCode.Error, handler.Status);
        Assert.Equal(typeof(InvalidOperationException).FullName, Tag(handler, TcjDiagnosticNames.Tags.ExceptionType));
        Assert.DoesNotContain(handler.TagObjects, tag => tag.Key == TcjDiagnosticNames.Tags.ExceptionMessage);
        Assert.DoesNotContain(TokenMarker, string.Join('\n', handler.TagObjects.Select(static tag => tag.Value?.ToString())));
    }

    [Fact]
    public async Task Cancellation_is_distinguished_from_framework_failure()
    {
        using var collector = new ActivityCollector(TcjDiagnosticNames.Sources.Core);
        using var cancellationSource = new CancellationTokenSource();
        var invoker = new DomainEventHandlerInvoker<TestEvent>(
            new IDomainEventHandler<TestEvent>[] { new CancelingHandler(cancellationSource) });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => invoker.InvokeAsync(
                new TestEvent(DateTimeOffset.UtcNow, PasswordMarker),
                cancellationSource.Token));

        Activity handler = Assert.Single(
            collector.Activities,
            activity => activity.OperationName == TcjDiagnosticNames.Activities.DomainEventHandle);
        Activity dispatch = Assert.Single(
            collector.Activities,
            activity => activity.OperationName == TcjDiagnosticNames.Activities.DomainEventDispatch);

        Assert.Equal(true, Tag(handler, TcjDiagnosticNames.Tags.Canceled));
        Assert.Equal(true, Tag(dispatch, TcjDiagnosticNames.Tags.Canceled));
        Assert.NotEqual(ActivityStatusCode.Error, handler.Status);
        Assert.NotEqual(ActivityStatusCode.Error, dispatch.Status);
    }

    private static object? Tag(Activity activity, string name) =>
        activity.TagObjects.FirstOrDefault(tag => tag.Key == name).Value;

    public void Dispose() => TcjTelemetry.ResetForTests();

    private sealed record TestEvent(DateTimeOffset OccurredOn, string Secret) : IDomainEvent;

    private sealed class SuccessHandler : IDomainEventHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent domainEvent, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailingHandler(Exception exception) : IDomainEventHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent domainEvent, CancellationToken cancellationToken) =>
            Task.FromException(exception);
    }

    private sealed class CancelingHandler(CancellationTokenSource source) : IDomainEventHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent domainEvent, CancellationToken cancellationToken)
        {
            source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
