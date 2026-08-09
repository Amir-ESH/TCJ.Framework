using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TCJ.AspNetCore.Diagnostics;
using TCJ.AspNetCore.Options;
using TCJ.Core.Diagnostics;

namespace TCJ.Observability.Tests;

public sealed class AspNetCoreTelemetryTests : IDisposable
{
    private const string SecretMarker = "TCJ_TEST_PASSWORD_MARKER";

    public AspNetCoreTelemetryTests() => TcjTelemetry.ResetForTests();

    [Fact]
    public async Task Exception_handler_is_child_of_request_and_hides_sensitive_exception_details()
    {
        using var collector = new ActivityCollector(TcjDiagnosticNames.Sources.AspNetCore);
        using var request = new Activity("Microsoft.AspNetCore.Hosting.HttpRequestIn").Start();
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/orders/42";
        var handler = new TcjExceptionHandler(
            NullLogger<TcjExceptionHandler>.Instance,
            Options.Create(new TcjAspNetCoreOptions()));
        var exception = new InvalidOperationException($"secret={SecretMarker}");

        bool handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled);
        Activity activity = Assert.Single(
            collector.Activities,
            item => item.OperationName == TcjDiagnosticNames.Activities.AspNetCoreExceptionHandle);
        Assert.Equal(request.TraceId, activity.TraceId);
        Assert.Equal(request.SpanId, activity.ParentSpanId);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(StatusCodes.Status500InternalServerError,
            activity.TagObjects.First(tag => tag.Key == TcjDiagnosticNames.Tags.HttpStatusCode).Value);
        Assert.DoesNotContain(activity.TagObjects, tag => tag.Key == TcjDiagnosticNames.Tags.ExceptionMessage);
        Assert.DoesNotContain(SecretMarker,
            string.Join('\n', activity.TagObjects.Select(static tag => tag.Value?.ToString())),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Default_exception_log_does_not_include_sensitive_exception_message()
    {
        var logger = new CaptureLogger<TcjExceptionHandler>();
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/orders/TCJ_TEST_TOKEN_MARKER";
        var handler = new TcjExceptionHandler(
            logger,
            Options.Create(new TcjAspNetCoreOptions()));

        await handler.TryHandleAsync(
            context,
            new InvalidOperationException($"secret={SecretMarker}"),
            CancellationToken.None);

        string message = Assert.Single(logger.Messages);
        Assert.DoesNotContain(SecretMarker, message, StringComparison.Ordinal);
        Assert.DoesNotContain("TCJ_TEST_TOKEN_MARKER", message, StringComparison.Ordinal);
        Assert.Null(logger.LastException);
    }

    [Fact]
    public async Task Canceled_request_is_not_classified_as_internal_error()
    {
        using var collector = new ActivityCollector(TcjDiagnosticNames.Sources.AspNetCore);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var context = new DefaultHttpContext
        {
            RequestAborted = cancellationSource.Token
        };
        var handler = new TcjExceptionHandler(
            NullLogger<TcjExceptionHandler>.Instance,
            Options.Create(new TcjAspNetCoreOptions()));

        bool handled = await handler.TryHandleAsync(
            context,
            new OperationCanceledException(cancellationSource.Token),
            cancellationSource.Token);

        Assert.True(handled);
        Activity activity = Assert.Single(
            collector.Activities,
            item => item.OperationName == TcjDiagnosticNames.Activities.AspNetCoreExceptionHandle);
        Assert.Equal(true, activity.TagObjects.First(tag => tag.Key == TcjDiagnosticNames.Tags.Canceled).Value);
        Assert.NotEqual(ActivityStatusCode.Error, activity.Status);
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];

        internal IReadOnlyList<string> Messages => _messages;

        internal Exception? LastException { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _messages.Add(formatter(state, exception));
            LastException = exception;
        }
    }

    public void Dispose() => TcjTelemetry.ResetForTests();
}
