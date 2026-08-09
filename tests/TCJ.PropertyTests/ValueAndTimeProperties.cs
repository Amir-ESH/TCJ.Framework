using FsCheck.Xunit;
using TCJ.Core.Extensions;
using TCJ.Core.Identifiers;
using TCJ.PropertyTests.Infrastructure;

namespace TCJ.PropertyTests;

public sealed class ValueAndTimeProperties
{
    [Property(MaxTest = 100, Replay = "1301,2301", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "DateTime")]
    public bool WeekdayAndWeekendAreComplements(BoundaryDateTime date)
        => date.Value.DayOfWeek.IsWeekend() != date.Value.DayOfWeek.IsWeekday();

    [Property(MaxTest = 100, Replay = "1302,2303", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "DateTime")]
    public bool LeapYearGeneratorProducesFebruaryTwentyNinth(LeapYearDate date)
        => DateTime.IsLeapYear(date.Value.Year) && date.Value.Month == 2 && date.Value.Day == 29;

    [Property(MaxTest = 100, Replay = "1303,2305", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "Decimal")]
    public bool RoundUpAndDownBoundValue(BoundaryDecimal input, byte rawPlaces)
    {
        int places = rawPlaces % 7;
        decimal value = input.Value;
        // Boundary generator is deliberately divided by ten to keep multiplication in range.
        decimal down = value.RoundDown(places);
        decimal up = value.RoundUp(places);
        return down <= value && value <= up;
    }

    [Property(MaxTest = 100, Replay = "1304,2307", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "Decimal")]
    public bool TruncateIsIdempotent(BoundaryDecimal input, byte rawPlaces)
    {
        int places = rawPlaces % 7;
        decimal once = input.Value.Truncate(places);
        return once.Truncate(places) == once;
    }

    [Property(MaxTest = 100, Replay = "1305,2309", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "Comparable")]
    public bool ComparableRangeMatchesInclusiveOrdering(ComparableValue value, ComparableValue first, ComparableValue second)
    {
        ComparableValue min = first.CompareTo(second) <= 0 ? first : second;
        ComparableValue max = first.CompareTo(second) <= 0 ? second : first;
        return value.IsBetween(min, max) == (value.CompareTo(min) >= 0 && value.CompareTo(max) <= 0);
    }

    [Property(MaxTest = 100, Replay = "1306,2311", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "DateTime")]
    public bool DateTimeOffsetRoundTripPreservesInstant(BoundaryDateTimeOffset value)
        => DateTimeOffset.Parse(value.Value.ToString("O"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).UtcDateTime == value.Value.UtcDateTime;

    [Property(MaxTest = 100, Replay = "1307,2313", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "Guid")]
    public bool GeneratedGuidRoundTrips(GeneratedGuid value)
        => Guid.Parse(value.Value.ToString("D")) == value.Value;

    [Property(MaxTest = 100, Replay = "1308,2315", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "Guid")]
    public bool VersionSevenGuidUsesDeterministicTime(long offsetSeconds)
    {
        long maxOffsetSeconds = (DateTimeOffset.MaxValue - DateTimeOffset.UnixEpoch).Ticks / TimeSpan.TicksPerSecond;
        long bounded = Math.Abs(offsetSeconds % (maxOffsetSeconds + 1));
        var provider = new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(bounded));
        var generator = new GuidGenerator(provider);
        Guid first = generator.CreateVersion7();
        Guid second = generator.CreateVersion7();
        string firstText = first.ToString("N");
        string secondText = second.ToString("N");
        return first != Guid.Empty && second != Guid.Empty
            && firstText[12] == '7' && secondText[12] == '7'
            && firstText[..12] == secondText[..12];
    }

    [Property(MaxTest = 100, Replay = "1309,2317", Arbitrary = new[] { typeof(PropertyArbitraries) })]
    [Trait("Category", "Property")]
    [Trait("Category", "Guid")]
    public bool VersionSevenGenerationDoesNotDuplicateWithinSample(byte rawCount)
    {
        int count = 2 + (rawCount % 31);
        var generator = new GuidGenerator(new FixedTimeProvider(DateTimeOffset.UnixEpoch));
        Guid[] values = Enumerable.Range(0, count).Select(_ => generator.CreateVersion7()).ToArray();
        return values.All(static value => value != Guid.Empty) && values.Distinct().Count() == count;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
