using System.Text;
using TCJ.Core.Results;

namespace TCJ.FuzzTests.Targets;

internal sealed class ResultCompositionTarget : IFuzzTarget
{
    public string Name => "ResultComposition";

    public void Execute(ReadOnlyMemory<byte> input)
    {
        string text = Encoding.UTF8.GetString(input.Span);
        bool fail = input.Length > 0 && (input.Span[0] & 1) != 0;
        Result<string> result = fail
            ? Result.Failure<string>(new ResultError("fuzz.failure", "Expected fuzz failure"))
            : Result.Success(text);

        var calls = 0;
        Result<int> mapped = result.Map(value => { calls++; return value.Length; });
        if (fail && (calls != 0 || !mapped.IsFailure))
            throw new FuzzInvariantException("Failure mapping invoked success state.");
        if (!fail && (calls != 1 || !mapped.IsSuccess || mapped.Value != text.Length))
            throw new FuzzInvariantException("Success mapping invariant failed.");
    }
}
