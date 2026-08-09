using FsCheck.Xunit;
using TCJ.Core.Extensions;
using TCJ.PropertyTests.Infrastructure;

namespace TCJ.PropertyTests;

public sealed class StringProperties
{
    [Property(MaxTest = 100, Replay = "1101,2101", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "String")]
    public bool NormalizeLineEndingsIsIdempotent(UnicodeText text)
    {
        string once = text.Value.NormalizeLineEndings();
        return once.NormalizeLineEndings() == once;
    }

    [Property(MaxTest = 100, Replay = "1102,2103", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "String")]
    public bool EnsureStartsWithAlwaysEstablishesPrefix(UnicodeText text, char prefix)
        => text.Value.EnsureStartsWith(prefix).StartsWith(prefix);

    [Property(MaxTest = 100, Replay = "1103,2105", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "String")]
    public bool EnsureEndsWithAlwaysEstablishesSuffix(UnicodeText text, char suffix)
        => text.Value.EnsureEndsWith(suffix).EndsWith(suffix);

    [Property(MaxTest = 100, Replay = "1104,2107", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "String")]
    public bool TruncateNeverExceedsRequestedLength(LongText text, byte rawLength)
    {
        int maxLength = rawLength;
        string? truncated = text.Value.Truncate(maxLength);
        return truncated is not null && truncated.Length <= maxLength;
    }

    [Property(MaxTest = 100, Replay = "1105,2109", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "String")]
    public bool NullIfWhitespaceRecognizesWhitespace(WhitespaceText text)
        => text.Value.NullIfWhiteSpace() is null;

    [Property(MaxTest = 100, Replay = "1106,2111", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "String")]
    public bool RemoveKnownPrefixRemovesExactlyOnePrefix(AsciiText text, char prefix)
    {
        string withPrefix = text.Value.EnsureStartsWith(prefix);
        return withPrefix.RemovePrefix(prefix.ToString()) == (text.Value.StartsWith(prefix) ? text.Value[1..] : text.Value);
    }
}
