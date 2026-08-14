using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TCJ.AspNetCore.Options;
using TCJ.Core.Diagnostics;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace TCJ.AspNetCore.Diagnostics;

/// <summary>
/// Converts unexpected request exceptions to safe Problem Details responses.
/// </summary>
public sealed class TcjExceptionHandler(ILogger<TcjExceptionHandler> logger, IOptions<TcjAspNetCoreOptions> options) : IExceptionHandler
{
    private const string UnexpectedErrorCode = "UNEXPECTED_ERROR";

    private static readonly Action<ILogger, string, string, string, Exception?> LogUnhandledException =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Error,
            new EventId(0),
            "Unhandled exception of type {ExceptionType} while processing {Method}. Trace identifier: {TraceIdentifier}");

    private readonly ILogger<TcjExceptionHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly TcjAspNetCoreOptions _options = options.Value ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        AspNetCoreTelemetryState telemetry = AspNetCoreTelemetryDiagnostics.StartExceptionHandling();

        if (exception is OperationCanceledException
            && httpContext.RequestAborted.IsCancellationRequested)
        {
            telemetry.CompleteCanceled(exception);
            return true;
        }

        try
        {
            // Do not include the exception object, message, request path, query string,
            // headers, or body in the default framework log. Consumers can attach richer
            // diagnostics explicitly at their application boundary when appropriate.
            LogUnhandledException(
                _logger,
                TcjTelemetry.NormalizeTypeName(exception.GetType()),
                httpContext.Request.Method,
                httpContext.TraceIdentifier,
                null);

            var problemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = _options.UnexpectedErrorTitle,
                Detail = _options.IncludeExceptionDetails
                    ? exception.Message
                    : _options.UnexpectedErrorDetail,
                Instance = httpContext.Request.Path,
                Extensions =
                {
                    ["code"] = UnexpectedErrorCode,
                    ["traceId"] = httpContext.TraceIdentifier
                }
            };

            await HttpResults.Problem(problemDetails)
                .ExecuteAsync(httpContext);

            telemetry.CompleteFailure(
                exception,
                StatusCodes.Status500InternalServerError,
                "unknown");

            return true;
        }
        catch (OperationCanceledException canceledException)
            when (cancellationToken.IsCancellationRequested || httpContext.RequestAborted.IsCancellationRequested)
        {
            telemetry.CompleteCanceled(canceledException);
            throw;
        }
        catch (Exception handlerException)
        {
            telemetry.CompleteFailure(
                handlerException,
                StatusCodes.Status500InternalServerError,
                "handler_failure");
            throw;
        }
    }
}
