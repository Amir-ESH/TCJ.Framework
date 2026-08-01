using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TCJ.AspNetCore.Options;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace TCJ.AspNetCore.Diagnostics;

/// <summary>
/// Converts unexpected request exceptions to safe Problem Details responses.
/// </summary>
public sealed class TcjExceptionHandler(ILogger<TcjExceptionHandler> logger, IOptions<TcjAspNetCoreOptions> options) : IExceptionHandler
{
    private const string UnexpectedErrorCode = "UNEXPECTED_ERROR";

    private readonly ILogger<TcjExceptionHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly TcjAspNetCoreOptions _options = options.Value ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OperationCanceledException
            && httpContext.RequestAborted.IsCancellationRequested)
        {
            return true;
        }

        _logger.LogError(exception, 
                         "Unhandled exception while processing {Method} {Path}. Trace identifier: {TraceIdentifier}", 
                         httpContext.Request.Method, 
                         httpContext.Request.Path, 
                         httpContext.TraceIdentifier);

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

        return true;
    }
}
