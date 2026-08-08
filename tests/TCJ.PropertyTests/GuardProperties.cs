using FsCheck.Xunit;
using TCJ.Core.Guards;
using TCJ.PropertyTests.Infrastructure;

namespace TCJ.PropertyTests;

public sealed class GuardProperties
{
    [Property(MaxTest = 100, Replay = "1401,2401", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "Guard")]
    public bool NotNullReturnsSameReference(UnicodeText text)
    {
        string value = text.Value;
        return ReferenceEquals(value, value.NotNull("value"));
    }

    [Property(MaxTest = 100, Replay = "1402,2402", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "Guard")]
    public bool NotNullOrWhitespaceRejectsGeneratedWhitespace(WhitespaceText text)
    {
        try { _ = text.Value.NotNullOrWhiteSpace("candidate"); return false; }
        catch (ArgumentException ex) { return ex.ParamName == "candidate"; }
    }

    [Property(MaxTest = 100, Replay = "1403,2403", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "Guard")]
    public bool PositiveAcceptsExactlyPositiveValues(int value)
    {
        try { _ = value.Positive("value"); return value > 0; }
        catch (ArgumentOutOfRangeException ex) { return value <= 0 && ex.ParamName == "value"; }
    }

    [Property(MaxTest = 100, Replay = "1404,2404", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "Guard")]
    public bool InRangeMatchesInclusiveComparison(int value, short a, short b)
    {
        int min = Math.Min(a, b);
        int max = Math.Max(a, b);
        try { _ = value.InRange(min, max, "value"); return value >= min && value <= max; }
        catch (ArgumentOutOfRangeException ex) { return (value < min || value > max) && ex.ParamName == "value"; }
    }

    [Property(MaxTest = 100, Replay = "1405,2405", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "Guard")]
    public bool LengthBetweenUsesInclusiveBounds(AsciiText text, byte lower, byte width)
    {
        int minimum = lower % 24;
        int maximum = minimum + (width % 24);
        try { _ = text.Value.LengthBetween(minimum, maximum, "text"); return text.Value.Length >= minimum && text.Value.Length <= maximum; }
        catch (ArgumentOutOfRangeException ex) { return (text.Value.Length < minimum || text.Value.Length > maximum) && ex.ParamName == "text"; }
    }
}
