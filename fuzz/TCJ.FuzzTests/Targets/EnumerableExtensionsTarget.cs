using TCJ.Core.Extensions;

namespace TCJ.FuzzTests.Targets;

internal sealed class EnumerableExtensionsTarget : IFuzzTarget
{
    private const int MaxElements = 4096;
    public string Name => "EnumerableExtensions";

    public void Execute(ReadOnlyMemory<byte> input)
    {
        int[] values = input.Span[..Math.Min(input.Length, MaxElements)].ToArray().Select(static b => (int)(sbyte)b).ToArray();
        int[] snapshot = values.ToArray();
        int[] even = values.WhereIf(true, static value => (value & 1) == 0).ToArray();
        if (even.Any(static value => (value & 1) != 0) || !values.SequenceEqual(snapshot))
            throw new FuzzInvariantException("WhereIf changed source data or returned an invalid element.");

        var collection = values.ToList();
        int candidate = values.Length == 0 ? 0 : values[0];
        bool existed = collection.Contains(candidate);
        int before = collection.Count;
        bool added = collection.AddIfNotContains(candidate);
        if (added == existed || collection.Count != before + (existed ? 0 : 1))
            throw new FuzzInvariantException("AddIfNotContains invariant failed.");
    }
}
