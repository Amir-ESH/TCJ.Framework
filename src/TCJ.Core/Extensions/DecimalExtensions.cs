namespace TCJ.Core.Extensions;
/// <summary>
/// Provides decimal rounding and truncation helpers.
/// </summary>

public static class DecimalExtensions
{
    private const int DefaultDecimalPlaces = 4;

    private static readonly decimal[] PowersOfTen =
    [
        1m,
        10m,
        100m,
        1_000m,
        10_000m,
        100_000m,
        1_000_000m,
        10_000_000m,
        100_000_000m,
        1_000_000_000m,
        10_000_000_000m,
        100_000_000_000m,
        1_000_000_000_000m,
        10_000_000_000_000m,
        100_000_000_000_000m,
        1_000_000_000_000_000m,
        10_000_000_000_000_000m,
        100_000_000_000_000_000m,
        1_000_000_000_000_000_000m,
        10_000_000_000_000_000_000m,
        100_000_000_000_000_000_000m,
        1_000_000_000_000_000_000_000m,
        10_000_000_000_000_000_000_000m,
        100_000_000_000_000_000_000_000m,
        1_000_000_000_000_000_000_000_000m,
        10_000_000_000_000_000_000_000_000m,
        100_000_000_000_000_000_000_000_000m,
        1_000_000_000_000_000_000_000_000_000m,
        10_000_000_000_000_000_000_000_000_000m
    ];
    /// <summary>
    /// Rounds the value up to the specified number of decimal places.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="decimalPlaces">The number of decimal places to retain.</param>
    /// <returns>The resulting value.</returns>

    public static decimal RoundUp(
        this decimal value,
        int decimalPlaces = DefaultDecimalPlaces)
    {
        var scale = GetScale(decimalPlaces);
        return Math.Ceiling(value * scale) / scale;
    }
    /// <summary>
    /// Rounds the value down to the specified number of decimal places.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="decimalPlaces">The number of decimal places to retain.</param>
    /// <returns>The resulting value.</returns>

    public static decimal RoundDown(
        this decimal value,
        int decimalPlaces = DefaultDecimalPlaces)
    {
        var scale = GetScale(decimalPlaces);
        return Math.Floor(value * scale) / scale;
    }
    /// <summary>
    /// Truncates the value to the requested maximum precision or length.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="decimalPlaces">The number of decimal places to retain.</param>
    /// <returns>The resulting value.</returns>

    public static decimal Truncate(
        this decimal value,
        int decimalPlaces = DefaultDecimalPlaces)
    {
        var scale = GetScale(decimalPlaces);
        return Math.Truncate(value * scale) / scale;
    }

    private static decimal GetScale(int decimalPlaces)
    {
        if ((uint)decimalPlaces >= (uint)PowersOfTen.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(decimalPlaces),
                decimalPlaces,
                "Decimal places must be between 0 and 28.");
        }

        return PowersOfTen[decimalPlaces];
    }
}
