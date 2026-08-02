using System.Globalization;
using TCJ.Core.Results;

namespace TCJ.Core.Tests;

public sealed class ResultBehaviorTests
{
    [Fact]
    public void Constructor_rejects_null_error_collection_and_null_entries()
    {
        Assert.Throws<ArgumentNullException>(() => new TestResult(isSuccess: true, errors: null!));
        Assert.Throws<ArgumentException>(() => new TestResult(isSuccess: false, errors: [null!]));
    }

    [Fact]
    public void Failure_exposes_first_error_and_preserves_error_order()
    {
        ResultError first = CommonErrors.Validation("first");
        ResultError second = CommonErrors.Conflict("second");

        Result result = Result.Failure([first, second]);

        Assert.True(result.IsFailure);
        Assert.False(result.IsSuccess);
        Assert.Same(first, result.FirstError);
        Assert.Equal(new[] { first, second }, result.Errors);
    }

    [Fact]
    public void Failure_factories_reject_null_errors()
    {
        Assert.Throws<ArgumentNullException>(() => Result.Failure(error: null!));
        Assert.Throws<ArgumentNullException>(() => Result.Failure(errors: null!));
        Assert.Throws<ArgumentNullException>(() => Result.Failure<int>(error: null!));
        Assert.Throws<ArgumentNullException>(() => Result.Failure<int>(errors: null!));
    }

    [Fact]
    public void Generic_success_exposes_value_and_ignores_fallback()
    {
        Result<int> result = Result.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Equal(42, result.GetValueOrDefault(-1));
    }

    [Fact]
    public void Map_does_not_invoke_mapper_for_failure()
    {
        bool invoked = false;
        Result<int> failed = Result.Failure<int>(CommonErrors.Failure("failed"));

        Result<string> mapped = failed.Map(value =>
        {
            invoked = true;
            return value.ToString(CultureInfo.InvariantCulture);
        });

        Assert.False(invoked);
        Assert.True(mapped.IsFailure);
        Assert.Equal(failed.Errors, mapped.Errors);
    }

    [Fact]
    public void Map_and_bind_reject_null_delegates()
    {
        Result<int> result = Result.Success(1);

        Assert.Throws<ArgumentNullException>(() => result.Map<string>(null!));
        Assert.Throws<ArgumentNullException>(() => result.Bind<string>(null!));
    }

    [Fact]
    public void Bind_rejects_a_null_result()
    {
        Result<int> result = Result.Success(1);

        Assert.Throws<InvalidOperationException>(() => result.Bind<string>(_ => null!));
    }

    [Fact]
    public void Bind_does_not_invoke_binder_for_failure()
    {
        bool invoked = false;
        Result<int> failed = Result.Failure<int>(CommonErrors.Failure("failed"));

        Result<string> bound = failed.Bind(value =>
        {
            invoked = true;
            return Result.Success(value.ToString(CultureInfo.InvariantCulture));
        });

        Assert.False(invoked);
        Assert.True(bound.IsFailure);
        Assert.Equal(failed.Errors, bound.Errors);
    }

    [Fact]
    public void Ensure_returns_same_instance_for_failure_and_matching_success()
    {
        Result<int> failed = Result.Failure<int>(CommonErrors.Failure("failed"));
        Result<int> success = Result.Success(10);
        ResultError error = CommonErrors.Validation("must be positive");

        Assert.Same(failed, failed.Ensure(_ => false, error));
        Assert.Same(success, success.Ensure(value => value > 0, error));
    }

    [Fact]
    public void Ensure_converts_non_matching_success_to_failure()
    {
        ResultError error = CommonErrors.Validation("must be positive");

        Result<int> result = Result.Success(-1).Ensure(value => value > 0, error);

        Assert.True(result.IsFailure);
        Assert.Same(error, Assert.Single(result.Errors));
    }

    [Fact]
    public void Ensure_rejects_null_arguments()
    {
        Result<int> result = Result.Success(1);

        Assert.Throws<ArgumentNullException>(() => result.Ensure(null!, CommonErrors.Failure("x")));
        Assert.Throws<ArgumentNullException>(() => result.Ensure(_ => true, null!));
    }

    [Fact]
    public void Match_selects_exactly_one_branch()
    {
        Result success = Result.Success();
        Result failure = Result.Failure(CommonErrors.Failure("failed"));
        Result<int> genericSuccess = Result.Success(7);
        Result<int> genericFailure = Result.Failure<int>(CommonErrors.Failure("failed"));

        Assert.Equal("success", success.Match(() => "success", _ => "failure"));
        Assert.Equal("failure", failure.Match(() => "success", _ => "failure"));
        Assert.Equal(14, genericSuccess.Match(value => value * 2, _ => -1));
        Assert.Equal(-1, genericFailure.Match(value => value * 2, _ => -1));
    }

    [Fact]
    public void Match_rejects_null_branches()
    {
        Result result = Result.Success();
        Result<int> generic = Result.Success(1);

        Assert.Throws<ArgumentNullException>(() => result.Match<string>(null!, _ => "failure"));
        Assert.Throws<ArgumentNullException>(() => result.Match(() => "success", null!));
        Assert.Throws<ArgumentNullException>(() => generic.Match<string>(null!, _ => "failure"));
        Assert.Throws<ArgumentNullException>(() => generic.Match(_ => "success", null!));
    }

    [Fact]
    public void Switch_selects_exactly_one_branch()
    {
        var calls = new List<string>();

        Result.Success().Switch(
            () => calls.Add("success"),
            _ => calls.Add("failure"));
        Result.Failure(CommonErrors.Failure("failed")).Switch(
            () => calls.Add("success"),
            _ => calls.Add("failure"));
        Result.Success(3).Switch(
            value => calls.Add($"value:{value}"),
            _ => calls.Add("generic-failure"));
        Result.Failure<int>(CommonErrors.Failure("failed")).Switch(
            value => calls.Add($"value:{value}"),
            _ => calls.Add("generic-failure"));

        Assert.Equal(new[] { "success", "failure", "value:3", "generic-failure" }, calls);
    }

    [Fact]
    public void Tap_and_tap_failure_run_only_for_their_matching_state()
    {
        int successCalls = 0;
        int failureCalls = 0;
        int observedValue = 0;

        Result.Success().Tap(() => successCalls++).TapFailure(_ => failureCalls++);
        Result.Failure(CommonErrors.Failure("failed"))
            .Tap(() => successCalls++)
            .TapFailure(_ => failureCalls++);
        Result.Success(9)
            .Tap(value => observedValue = value)
            .TapFailure(_ => failureCalls++);
        Result.Failure<int>(CommonErrors.Failure("failed"))
            .Tap(_ => successCalls++)
            .TapFailure(_ => failureCalls++);

        Assert.Equal(1, successCalls);
        Assert.Equal(2, failureCalls);
        Assert.Equal(9, observedValue);
    }

    [Fact]
    public void Combine_rejects_null_collection_and_null_item()
    {
        Assert.Throws<ArgumentNullException>(() => Result.Combine((IEnumerable<Result>)null!));
        Assert.Throws<ArgumentException>(() => Result.Combine([Result.Success(), null!]));
    }

    [Fact]
    public void Combine_returns_success_when_every_result_succeeds()
    {
        Result combined = Result.Combine(Result.Success(), Result.Success());

        Assert.True(combined.IsSuccess);
        Assert.Empty(combined.Errors);
    }

    [Fact]
    public void Result_error_validates_identity_and_formats_output()
    {
        Assert.Throws<ArgumentException>(() => new ResultError("", "message"));
        Assert.Throws<ArgumentException>(() => new ResultError("CODE", ""));

        var error = new ResultError("CODE", "message", ResultErrorType.Validation);

        Assert.Equal("CODE", error.Code);
        Assert.Equal("message", error.Message);
        Assert.Equal(ResultErrorType.Validation, error.Type);
        Assert.Equal("[CODE] message", error.ToString());
    }

    [Fact]
    public void Result_error_equality_uses_code_message_and_type_but_not_metadata()
    {
        ResultError left = new ResultError("CODE", "message", ResultErrorType.Conflict)
            .WithMetadata("left", 1);
        ResultError right = new ResultError("CODE", "message", ResultErrorType.Conflict)
            .WithMetadata("right", 2);
        ResultError different = new ResultError("OTHER", "message", ResultErrorType.Conflict);

        Assert.Equal(left, right);
        Assert.True(left == right);
        Assert.False(left != right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.NotEqual(left, different);
        Assert.False(left.Equals((object?)null));
    }

    [Fact]
    public void With_metadata_overwrites_key_on_a_new_instance()
    {
        ResultError original = CommonErrors.Validation("invalid").WithMetadata("Field", "Old");

        ResultError updated = original.WithMetadata("Field", "New");

        Assert.Equal("Old", original.Metadata["Field"]);
        Assert.Equal("New", updated.Metadata["Field"]);
        Assert.Throws<ArgumentException>(() => original.WithMetadata("", 1));
    }

    [Fact]
    public void Common_errors_have_stable_codes_types_and_metadata()
    {
        ResultError validation = CommonErrors.ValidationForField("Name", "required");
        ResultError notFound = CommonErrors.NotFound("Order", 42);

        Assert.Equal("VALIDATION_FAILED", validation.Code);
        Assert.Equal(ResultErrorType.Validation, validation.Type);
        Assert.Equal("Name", validation.Metadata["FieldName"]);

        Assert.Equal("NOT_FOUND", notFound.Code);
        Assert.Equal(ResultErrorType.NotFound, notFound.Type);
        Assert.Equal("Order", notFound.Metadata["EntityName"]);
        Assert.Equal(42, notFound.Metadata["Id"]);

        Assert.Equal("CONFLICT", CommonErrors.Conflict("duplicate").Code);
        Assert.Equal("UNAUTHORIZED", CommonErrors.Unauthorized().Code);
        Assert.Equal("FORBIDDEN", CommonErrors.Forbidden().Code);
        Assert.Equal("UNEXPECTED_ERROR", CommonErrors.Unexpected().Code);
        Assert.Throws<ArgumentException>(() => CommonErrors.ValidationForField("", "required"));
        Assert.Throws<ArgumentException>(() => CommonErrors.NotFound("", 1));
    }

    private sealed class TestResult(bool isSuccess, IEnumerable<ResultError> errors)
        : Result(isSuccess, errors);
}
