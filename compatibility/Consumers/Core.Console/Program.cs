using TCJ.Core.Extensions;
using TCJ.Core.Guards;
using TCJ.Core.Identifiers;
using TCJ.Core.Results;

Result<int> success = Result.Success(42);
Result<int> failure = Result.Failure<int>(CommonErrors.Validation("invalid input"));

if (!success.IsSuccess || success.Value != 42 || !failure.IsFailure || failure.Errors.Count != 1)
{
    throw new InvalidOperationException("Result behavior is invalid.");
}

if (Check.NotNullOrWhiteSpace("TCJ") != "TCJ")
{
    throw new InvalidOperationException("Guard behavior is invalid.");
}

if ("consumer".EnsureEndsWith('!') != "consumer!" || !DayOfWeek.Saturday.IsWeekend())
{
    throw new InvalidOperationException("Extension behavior is invalid.");
}

IGuidGenerator guidGenerator = new GuidGenerator(TimeProvider.System);
Guid guid = guidGenerator.CreateVersion7();
if (guid == Guid.Empty || TimeProvider.System.GetUtcNow() == default)
{
    throw new InvalidOperationException("Time/GUID abstractions are invalid.");
}

Console.WriteLine("TCJ.Core consumer passed");
