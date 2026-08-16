namespace TCJ.Core.Extensions;
/// <summary>
/// Provides comparison-oriented extension methods.
/// </summary>

public static class ComparableExtensions
{
    /// <summary>
    /// Determines whether a value falls within the inclusive range.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="minimumInclusive">The inclusive minimum value.</param>
    /// <param name="maximumInclusive">The inclusive maximum value.</param>
    /// <param name="comparer">An optional comparer; when omitted, the default comparer is used.</param>
    /// <returns>true when the condition is satisfied; otherwise, false.</returns>
    public static bool IsBetween<T>(
        this T value,
        T minimumInclusive,
        T maximumInclusive,
        IComparer<T>? comparer = null)
    {
        comparer ??= Comparer<T>.Default;

        if (comparer.Compare(minimumInclusive, maximumInclusive) > 0)
        {
            throw new ArgumentException(
                "Minimum value cannot be greater than maximum value.",
                nameof(minimumInclusive));
        }

        return comparer.Compare(value, minimumInclusive) >= 0
            && comparer.Compare(value, maximumInclusive) <= 0;
    }
}
