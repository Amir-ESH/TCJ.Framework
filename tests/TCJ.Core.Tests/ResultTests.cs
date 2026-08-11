using TCJ.Core.Results;

namespace TCJ.Core.Tests;

public sealed class ResultTests
{
	// Required PR Gate recovery smoke test.
	
	[Fact]
	public void Required_gate_recovery_intentional_failure()
	{
		Assert.True(
			false,
			"Intentional failure for Required PR Gate recovery test.");
	}
	
    [Fact]
    public void Success_has_no_errors()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Empty(result.Errors);
        Assert.Null(result.FirstError);
    }

    [Fact]
    public void Failure_requires_at_least_one_error()
    {
        Assert.Throws<ArgumentException>(() => new TestResult(isSuccess: false, errors: []));
    }

    [Fact]
    public void Success_cannot_contain_errors()
    {
        Assert.Throws<ArgumentException>(() => new TestResult(isSuccess: true, errors: [CommonErrors.Failure(message: "boom")]));
    }

    [Fact]
    public void Failed_generic_result_does_not_expose_a_value()
    {
        var result = Result.Failure<int>(error: CommonErrors.NotFound("Number", id: 42));

        Assert.True(result.IsFailure);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = result.Value;
        });

        Assert.Equal(-1, result.GetValueOrDefault(-1));
    }

    [Fact]
    public void Map_and_bind_transform_only_successful_results()
    {
        var successful = Result.Success(21);
        Result<string> mapped = successful.Map(value => (value * 2).ToString());
        Result<int> bound = successful.Bind(value => Result.Success(value + 1));

        Assert.Equal("42", mapped.Value);
        Assert.Equal(22, bound.Value);

        var failed = Result.Failure<int>(error: CommonErrors.Conflict(message: "duplicate"));

        Assert.True(failed.Map(value => value * 2).IsFailure);
        Assert.True(failed.Bind(value => Result.Success(value * 2)).IsFailure);
    }

    [Fact]
    public void Combine_aggregates_all_failures_in_input_order()
    {
        var first = Result.Failure(CommonErrors.Validation(message: "first"));
        var second = Result.Success();
        var third = Result.Failure(CommonErrors.Conflict(message: "third"));

        var combined = Result.Combine(first, second, third);

        Assert.True(combined.IsFailure);
        Assert.Collection(combined.Errors, error => Assert.Equal("first", error.Message),
                          error => Assert.Equal("third", error.Message));
    }

    [Fact]
    public void Adding_metadata_returns_a_new_immutable_error()
    {
        ResultError original = CommonErrors.Validation(message: "Name is required.");
        ResultError enriched = original.WithMetadata("FieldName", "Name");

        Assert.Empty(original.Metadata);
        Assert.Equal("Name", enriched.Metadata["FieldName"]);
        Assert.Equal(original, enriched);
    }

    private sealed class TestResult(bool isSuccess, IEnumerable<ResultError> errors) : Result(isSuccess, errors);
}
