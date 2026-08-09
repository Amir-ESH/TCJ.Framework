using FsCheck.Xunit;
using TCJ.Core.Extensions;
using TCJ.PropertyTests.Infrastructure;

namespace TCJ.PropertyTests;

public sealed class EnumerableCollectionProperties
{
    [Property(MaxTest = 100, Replay = "1201,2201", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "Enumerable")]
    public bool WhereIfFalsePreservesSequence(IntSequence sequence)
        => sequence.Values.WhereIf(false, static value => value > 0).SequenceEqual(sequence.Values);

    [Property(MaxTest = 100, Replay = "1202,2203", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "Enumerable")]
    public bool WhereIfTrueMatchesLinq(IntSequence sequence, int threshold)
        => sequence.Values.WhereIf(true, value => value >= threshold)
                          .SequenceEqual(sequence.Values.Where(value => value >= threshold));

    [Property(MaxTest = 100, Replay = "1203,2205", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "Collection")]
    public bool AddIfNotContainsChangesCountAtMostOnce(DuplicateSequence sequence, int item)
    {
        var list = sequence.Values.ToList();
        int before = list.Count;
        bool existed = list.Contains(item);
        bool added = list.AddIfNotContains(item);
        return added == !existed && list.Count == before + (existed ? 0 : 1);
    }

    [Property(MaxTest = 100, Replay = "1204,2207", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "Collection")]
    public bool RemoveWhereLeavesNoMatchingValues(IntSequence sequence, int threshold)
    {
        var list = sequence.Values.ToList();
        IReadOnlyList<int> removed = list.RemoveWhere(value => value < threshold);
        return list.All(value => value >= threshold) && removed.All(value => value < threshold);
    }

    [Property(MaxTest = 100, Replay = "1205,2209", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "Enumerable")]
    public bool WhereIfDoesNotMutateSource(IntSequence sequence, bool condition)
    {
        int[] original = sequence.Values.ToArray();
        _ = sequence.Values.WhereIf(condition, static value => (value & 1) == 0).ToArray();
        return sequence.Values.SequenceEqual(original);
    }
}
