using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using HttpResults = Microsoft.AspNetCore.Http.Results;
using TCJ.Core.Results;

namespace TCJ.AspNetCore.Results;

/// <summary>
/// Maps framework-neutral results to ASP.NET Core HTTP results.
/// </summary>
public static class ResultHttpExtensions
{
    /// <summary>
    /// Maps a successful result to HTTP 204 and a failed result to Problem Details.
    /// </summary>
    public static IResult ToHttpResult(this Result result)
        => ToHttpResult(result, static () => HttpResults.NoContent());

    /// <summary>
    /// Maps a successful result using the supplied HTTP factory and a failed result to Problem Details.
    /// </summary>
    public static IResult ToHttpResult(this Result result, Func<IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onSuccess);

        return result.IsSuccess 
                   ? onSuccess() 
                   : CreateFailureResult(result.Errors);
    }

    /// <summary>
    /// Maps a successful result to HTTP 200 and a failed result to Problem Details.
    /// </summary>
    public static IResult ToHttpResult<T>(this Result<T> result)
        => ToHttpResult(result, static value => HttpResults.Ok(value));

    /// <summary>
    /// Maps a successful result using the supplied HTTP factory and a failed result to Problem Details.
    /// </summary>
    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onSuccess);

        return result.IsSuccess 
                   ? onSuccess(result.Value) 
                   : CreateFailureResult(result.Errors);
    }

    private static IResult CreateFailureResult(IReadOnlyList<ResultError> errors)
    {
        if (errors.All(static error => error.Type == ResultErrorType.Validation))
        {
            return CreateValidationProblem(errors);
        }

        int statusCode = ResolveStatusCode(errors);

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = ResolveTitle(statusCode),
            Detail = errors.Count == 1 
                         ? errors[0].Message 
                         : "One or more errors prevented the request from being completed.",
            Extensions =
            {
                ["errors"] = errors.Select(CreateErrorPayload).ToArray()
            }
        };

        return HttpResults.Problem(problemDetails);
    }

    private static IResult CreateValidationProblem(IReadOnlyList<ResultError> errors)
    {
        var validationErrors = errors.GroupBy(GetValidationField, StringComparer.Ordinal)
                                     .ToDictionary(static group => group.Key,
                                                   static group => group
                                                                   .Select(static error => error.Message)
                                                                   .Distinct(StringComparer.Ordinal)
                                                                   .ToArray(),
                                                   StringComparer.Ordinal);

        var validationCodes = errors
                              .GroupBy(GetValidationField, StringComparer.Ordinal)
                              .ToDictionary(static group => group.Key,
                                            static group => group
                                                            .Select(static error => error.Code)
                                                            .Distinct(StringComparer.Ordinal)
                                                            .ToArray(),
                                            StringComparer.Ordinal);

        var problemDetails = new HttpValidationProblemDetails(validationErrors)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation errors occurred.",
            Extensions =
            {
                ["errorCodes"] = validationCodes
            }
        };

        return HttpResults.Problem(problemDetails);
    }

    private static string GetValidationField(ResultError error)
    {
        if (error.Metadata.TryGetValue("FieldName", out object? fieldName) 
         && fieldName is string value
         && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return string.Empty;
    }

    private static Dictionary<string, object?> CreateErrorPayload(ResultError error) 
        => new(StringComparer.Ordinal) 
        {
            ["code"] = error.Code,
            ["message"] = error.Message,
            ["type"] = error.Type.ToString(),
            ["metadata"] = error.Metadata,
        };

    private static int ResolveStatusCode(IReadOnlyList<ResultError> errors)
    {
        if (errors.Any(static error => error.Type == ResultErrorType.Unexpected))
        {
            return StatusCodes.Status500InternalServerError;
        }

        if (errors.Any(static error => error.Type == ResultErrorType.Unauthorized))
        {
            return StatusCodes.Status401Unauthorized;
        }

        if (errors.Any(static error => error.Type == ResultErrorType.Forbidden))
        {
            return StatusCodes.Status403Forbidden;
        }

        if (errors.Any(static error => error.Type == ResultErrorType.Conflict))
        {
            return StatusCodes.Status409Conflict;
        }

        if (errors.Any(static error => error.Type == ResultErrorType.NotFound))
        {
            return StatusCodes.Status404NotFound;
        }

        return StatusCodes.Status400BadRequest;
    }

    private static string ResolveTitle(int statusCode)
        => statusCode switch
        {
            StatusCodes.Status400BadRequest => "The request could not be processed.",
            StatusCodes.Status401Unauthorized => "Authentication is required.",
            StatusCodes.Status403Forbidden => "Access is forbidden.",
            StatusCodes.Status404NotFound => "The requested resource was not found.",
            StatusCodes.Status409Conflict => "The request conflicts with the current state.",
            StatusCodes.Status500InternalServerError => "An unexpected server error occurred.",
            _ => "The request could not be completed.",
        };
}
