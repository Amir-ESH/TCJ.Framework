using System.Text;
using TCJ.Core.Guards;

namespace TCJ.FuzzTests.Targets;

internal sealed class CheckTarget : IFuzzTarget
{
    public string Name => "Check";

    public void Execute(ReadOnlyMemory<byte> input)
    {
        string value = Encoding.UTF8.GetString(input.Span);
        TryExpected(() => value.NotNullOrEmpty("fuzzValue"));
        TryExpected(() => value.NotNullOrWhiteSpace("fuzzValue"));
        TryExpected(() => value.LengthBetween(0, 256, "fuzzValue"));
        int numeric = input.Length == 0 ? 0 : unchecked((sbyte)input.Span[0]);
        TryExpected(() => numeric.Positive("numeric"));
        TryExpected(() => numeric.InRange(-64, 64, "numeric"));
    }

    private static void TryExpected(Action action)
    {
        try { action(); }
        catch (ArgumentException) { }
    }
}
