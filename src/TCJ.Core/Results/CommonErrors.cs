namespace TCJ.Core.Results;

/// <summary>
/// Creates common framework-neutral result errors.
/// </summary>
public static class CommonErrors
{
    public static ResultError Failure(string message, string code = "OPERATION_FAILED")
        => new(code, message, ResultErrorType.Failure);

    public static ResultError Validation(string message)
        => new(code: "VALIDATION_FAILED", message, ResultErrorType.Validation);

    public static ResultError ValidationForField(string fieldName, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);

        return Validation(message).WithMetadata("FieldName", fieldName);
    }

    public static ResultError NotFound(string entityName, object? id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);

        return new ResultError(code: "NOT_FOUND",
                               message: $"{entityName} with identifier '{id}' was not found.",
                               ResultErrorType.NotFound)
               .WithMetadata("EntityName", entityName)
               .WithMetadata("Id", id);
    }

    public static ResultError Conflict(string message)
        => new(code: "CONFLICT", message, ResultErrorType.Conflict);

    public static ResultError Unauthorized(string message = "Authentication is required.")
        => new(code: "UNAUTHORIZED", message, ResultErrorType.Unauthorized);

    public static ResultError Forbidden(string message = "Access is forbidden.")
        => new(code: "FORBIDDEN", message, ResultErrorType.Forbidden);

    public static ResultError Unexpected(string message = "An unexpected error occurred.")
        => new(code: "UNEXPECTED_ERROR", message, ResultErrorType.Unexpected);
}
