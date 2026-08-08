using FsCheck.Xunit;
using TCJ.Core.Results;
using TCJ.PropertyTests.Infrastructure;

namespace TCJ.PropertyTests;

public sealed class ResultProperties
{
    [Property(MaxTest = 100, Replay = "1001,2001", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "Result")]
    public bool SuccessPreservesValue(int value)
    {
        Result<int> result = Result.Success(value);
        return result.IsSuccess && !result.IsFailure && result.Value == value && result.Errors.Count == 0;
    }

    [Property(MaxTest = 100, Replay = "1002,2002", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "Result")]
    public bool FailurePreservesError(int value)
    {
        var error = new ResultError("property.failure", $"value={value}");
        Result<int> result = Result.Failure<int>(error);
        return result.IsFailure && result.FirstError == error && result.Errors.SequenceEqual([error]);
    }

    [Property(MaxTest = 100, Replay = "1003,2003", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "Result")]
    public bool SuccessfulMapRunsExactlyOnce(int value)
    {
        var calls = 0;
        Result<int> mapped = Result.Success(value).Map(input => { calls++; return input + 1; });
        return calls == 1 && mapped.IsSuccess && mapped.Value == value + 1;
    }

    [Property(MaxTest = 100, Replay = "1004,2004", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "Result")]
    public bool FailedMapDoesNotInvokeMapper(int value)
    {
        var calls = 0;
        Result<int> mapped = Result.Failure<int>(new ResultError("property.failure", "failure"))
                                   .Map(input => { calls++; return input + value; });
        return calls == 0 && mapped.IsFailure;
    }

    [Property(MaxTest = 100, Replay = "1005,2005", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "Result")]
    public bool FailedValueAccessUsesDocumentedException(int value)
    {
        _ = value;
        Result<int> failed = Result.Failure<int>(new ResultError("failure", "failure"));
        try { _ = failed.Value; return false; }
        catch (InvalidOperationException) { return true; }
    }

    [Property(MaxTest = 100, Replay = "1006,2006", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "Result")]
    public bool CombineDoesNotMutateInputs(bool firstFails, bool secondFails)
    {
        Result first = firstFails ? Result.Failure(new ResultError("first", "first")) : Result.Success();
        Result second = secondFails ? Result.Failure(new ResultError("second", "second")) : Result.Success();
        bool firstBefore = first.IsSuccess;
        bool secondBefore = second.IsSuccess;
        Result combined = Result.Combine(first, second);
        return first.IsSuccess == firstBefore && second.IsSuccess == secondBefore
            && combined.IsSuccess == (!firstFails && !secondFails);
    }
}
