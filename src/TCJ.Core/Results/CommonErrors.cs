namespace TCJ.Core.Results;

/// <summary>
/// Creates common framework-neutral result errors.
/// </summary>
public static class CommonErrors
{
    /// <summary>
    /// Creates a general failure error.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="code">The error code.</param>
    /// <returns>The result of the operation.</returns>
    public static ResultError Failure(string message, string code = "OPERATION_FAILED")
        => new(code, message, ResultErrorType.Failure);
    /// <summary>
    /// Creates a validation error.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <returns>The result of the operation.</returns>

    public static ResultError Validation(string message)
        => new(code: "VALIDATION_FAILED", message, ResultErrorType.Validation);
    /// <summary>
    /// Creates a validation error associated with a field.
    /// </summary>
    /// <param name="fieldName">The field associated with the validation error.</param>
    /// <param name="message">The error message.</param>
    /// <returns>The result of the operation.</returns>

    public static ResultError ValidationForField(string fieldName, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);

        return Validation(message).WithMetadata("FieldName", fieldName);
    }
    /// <summary>
    /// Creates a not-found error.
    /// </summary>
    /// <param name="entityName">The entity name.</param>
    /// <param name="id">The id value.</param>
    /// <returns>The result of the operation.</returns>

    public static ResultError NotFound(string entityName, object? id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);

        return new ResultError(code: "NOT_FOUND",
                               message: $"{entityName} with identifier '{id}' was not found.",
                               ResultErrorType.NotFound)
               .WithMetadata("EntityName", entityName)
               .WithMetadata("Id", id);
    }
    /// <summary>
    /// Creates a conflict error.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <returns>The result of the operation.</returns>

    public static ResultError Conflict(string message)
        => new(code: "CONFLICT", message, ResultErrorType.Conflict);
    /// <summary>
    /// Creates an unauthorized error.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <returns>The result of the operation.</returns>

    public static ResultError Unauthorized(string message = "Authentication is required.")
        => new(code: "UNAUTHORIZED", message, ResultErrorType.Unauthorized);
    /// <summary>
    /// Creates a forbidden error.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <returns>The result of the operation.</returns>

    public static ResultError Forbidden(string message = "Access is forbidden.")
        => new(code: "FORBIDDEN", message, ResultErrorType.Forbidden);
    /// <summary>
    /// Creates an unexpected-error result.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <returns>The result of the operation.</returns>

    public static ResultError Unexpected(string message = "An unexpected error occurred.")
        => new(code: "UNEXPECTED_ERROR", message, ResultErrorType.Unexpected);
}
