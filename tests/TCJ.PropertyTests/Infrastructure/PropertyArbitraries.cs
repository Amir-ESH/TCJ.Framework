using FsCheck;
using FsCheck.Fluent;
using Microsoft.Extensions.DependencyInjection;
using TCJ.PropertyTests;

namespace TCJ.PropertyTests.Infrastructure;

public sealed record UnicodeText(string Value);
public sealed record AsciiText(string Value);
public sealed record WhitespaceText(string Value);
public sealed record LongText(string Value);
public readonly record struct BoundaryDecimal(decimal Value);
public readonly record struct BoundaryDateTime(DateTime Value);
public readonly record struct ComparableValue(int Value) : IComparable<ComparableValue>
{
    public int CompareTo(ComparableValue other) => Value.CompareTo(other.Value);
}
public sealed record IntSequence(int[] Values);
public sealed record DuplicateSequence(int[] Values);
public sealed record NullableSequence(string?[] Values);
public sealed record CancellationCase(bool IsCancelled)
{
    public CancellationToken Token => IsCancelled ? new CancellationToken(canceled: true) : CancellationToken.None;
}
public readonly record struct BoundaryDateTimeOffset(DateTimeOffset Value);
public readonly record struct LeapYearDate(DateTime Value);
public readonly record struct GeneratedGuid(Guid Value);
public sealed record ServiceMarkerCase(Type ImplementationType, ServiceLifetime Lifetime);
public sealed record InvalidTypeCombination(Type[] MarkerTypes);
public sealed record ThrowingEnumerableCase(int ThrowAfter)
{
    public IEnumerable<int> Create()
    {
        for (var index = 0; ; index++)
        {
            if (index >= ThrowAfter) throw new InvalidOperationException("Generated enumerator failure.");
            yield return index;
        }
    }
}

public static class PropertyArbitraries
{
    private static readonly string[] UnicodeCases =
    [
        string.Empty, "ascii", " café ", "e\u0301", "\U0001F600", "مرحبا", "日本語", "فارسی",
        "a\r\nb\nc\r", "Straße", "İstanbul", "\uD800", "\uDC00", "👩‍💻"
    ];

    private static readonly string[] WhiteSpaceCases = [string.Empty, " ", "\t", "\r\n", " \t\r\n "];

    public static Arbitrary<UnicodeText> UnicodeText() =>
        Arb.From(
            Gen.Elements(UnicodeCases).Select(static value => new UnicodeText(value)),
            static value => ShrinkString(value.Value).Select(static text => new UnicodeText(text)));

    public static Arbitrary<AsciiText> AsciiText() =>
        Arb.From(
            Gen.ArrayOf(Gen.Choose(32, 126), 32)
               .Select(static chars => new AsciiText(new string(chars.Select(static value => (char)value).ToArray()))),
            static value => ShrinkString(value.Value).Select(static text => new AsciiText(text)));

    public static Arbitrary<WhitespaceText> WhitespaceText() =>
        Arb.From(Gen.Elements(WhiteSpaceCases).Select(static value => new WhitespaceText(value)),
                 static value => value.Value.Length == 0 ? [] : [new WhitespaceText(string.Empty)]);

    public static Arbitrary<LongText> LongText() =>
        Arb.From(
            Gen.Choose(0, 4096).Select(static length => new LongText(new string('x', length))),
            static value => value.Value.Length <= 1
                ? []
                : [new LongText(value.Value[..(value.Value.Length / 2)]), new LongText(string.Empty)]);

    public static Arbitrary<BoundaryDecimal> BoundaryDecimal() =>
        Arb.From(Gen.Elements(new[]
        {
            -100_000_000_000_000_000_000m, -1_000_000.50005m, -1m, -0.5m, 0m, 0.5m, 1m,
            1_000_000.50005m, 100_000_000_000_000_000_000m
        }).Select(static value => new BoundaryDecimal(value)),
        static value => value.Value == 0m ? [] : [new BoundaryDecimal(0m)]);

    public static Arbitrary<BoundaryDateTime> BoundaryDateTime() =>
        Arb.From(Gen.Elements(new[]
        {
            DateTime.MinValue.AddDays(2),
            new DateTime(2000, 2, 29, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2024, 2, 29, 0, 0, 0, DateTimeKind.Local),
            new DateTime(2025, 12, 31, 23, 59, 59, DateTimeKind.Unspecified),
            DateTime.MaxValue.AddDays(-2)
        }).Select(static value => new BoundaryDateTime(value)),
        static value => value.Value.Year == 2000
            ? []
            : [new BoundaryDateTime(new DateTime(2000, 2, 29, 12, 0, 0, DateTimeKind.Utc))]);

    public static Arbitrary<ComparableValue> ComparableValue() =>
        Arb.From(Gen.Choose(-1_000_000, 1_000_000).Select(static value => new ComparableValue(value)),
                 static value => value.Value == 0 ? [] : [new ComparableValue(0)]);

    public static Arbitrary<BoundaryDateTimeOffset> DateTimeOffsetBoundary() =>
        Arb.From(Gen.Elements(new[]
        {
            DateTimeOffset.MinValue.AddDays(2), DateTimeOffset.UnixEpoch,
            new DateTimeOffset(2000, 2, 29, 12, 0, 0, TimeSpan.Zero),
            DateTimeOffset.MaxValue.AddDays(-2)
        }).Select(static value => new BoundaryDateTimeOffset(value)),
        static value => value.Value == DateTimeOffset.UnixEpoch ? [] : [new BoundaryDateTimeOffset(DateTimeOffset.UnixEpoch)]);

    public static Arbitrary<LeapYearDate> LeapYearDate() =>
        Arb.From(Gen.Elements(new[]
        {
            new DateTime(2000, 2, 29), new DateTime(2004, 2, 29),
            new DateTime(2024, 2, 29), new DateTime(2400, 2, 29)
        }).Select(static value => new LeapYearDate(value)),
        static value => value.Value.Year == 2000 ? [] : [new LeapYearDate(new DateTime(2000, 2, 29))]);

    public static Arbitrary<GeneratedGuid> GuidValue() =>
        Arb.From(Gen.ArrayOf(Gen.Choose(0, 255), 16)
                    .Select(static values => new GeneratedGuid(new Guid(values.Select(static value => (byte)value).ToArray()))),
                 static value => value.Value == Guid.Empty ? [] : [new GeneratedGuid(Guid.Empty)]);

    public static Arbitrary<ServiceMarkerCase> ServiceTypeWithMarker() =>
        Arb.From(Gen.Elements(new[]
        {
            new ServiceMarkerCase(typeof(PropertyTransient), ServiceLifetime.Transient),
            new ServiceMarkerCase(typeof(PropertyScoped), ServiceLifetime.Scoped),
            new ServiceMarkerCase(typeof(PropertySingleton), ServiceLifetime.Singleton),
            new ServiceMarkerCase(typeof(PropertySelfScoped), ServiceLifetime.Scoped)
        }));

    public static Arbitrary<InvalidTypeCombination> InvalidTypeCombination() =>
        Arb.From(Gen.Elements(new[]
        {
            new InvalidTypeCombination([typeof(TCJ.DependencyInjection.Lifetimes.ITransientDependency), typeof(TCJ.DependencyInjection.Lifetimes.IScopedDependency)]),
            new InvalidTypeCombination([typeof(TCJ.DependencyInjection.Lifetimes.ISingletonDependency), typeof(TCJ.DependencyInjection.Lifetimes.ISelfSingletonDependency)])
        }));

    public static Arbitrary<ThrowingEnumerableCase> ExceptionProducingEnumerable() =>
        Arb.From(Gen.Choose(0, 16).Select(static value => new ThrowingEnumerableCase(value)),
                 static value => value.ThrowAfter == 0 ? [] : [new ThrowingEnumerableCase(0)]);

    public static Arbitrary<IntSequence> IntSequence() =>
        Arb.From(Gen.ArrayOf(Gen.Choose(-100, 100)).Select(static values => new IntSequence(values)),
                 static value => ShrinkArray(value.Values).Select(static values => new IntSequence(values)));

    public static Arbitrary<DuplicateSequence> DuplicateSequence() =>
        Arb.From(Gen.ArrayOf(Gen.Choose(-8, 8)).Select(static values => new DuplicateSequence(values)),
                 static value => ShrinkArray(value.Values).Select(static values => new DuplicateSequence(values)));

    public static Arbitrary<NullableSequence> NullableSequence() =>
        Arb.From(Gen.Elements(new[]
        {
            Array.Empty<string?>(),
            new string?[] { null },
            new string?[] { "a", null, "a" },
            new string?[] { string.Empty, "x", null, "x" }
        }).Select(static values => new NullableSequence(values)),
        static value => value.Values.Length == 0 ? [] : [new NullableSequence([])]);

    public static Arbitrary<CancellationCase> CancellationTokenCase() =>
        Arb.From(Gen.Elements(new[] { false, true }).Select(static value => new CancellationCase(value)));

    private static IEnumerable<string> ShrinkString(string value)
    {
        if (value.Length == 0)
        {
            yield break;
        }

        yield return string.Empty;
        if (value.Length > 1)
        {
            yield return value[..(value.Length / 2)];
        }
    }

    private static IEnumerable<int[]> ShrinkArray(int[] value)
    {
        if (value.Length == 0)
        {
            yield break;
        }

        yield return [];
        if (value.Length > 1)
        {
            yield return value[..(value.Length / 2)];
        }
    }
}
