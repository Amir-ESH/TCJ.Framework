namespace TCJ.Core.Extensions;

public static class ComparableExtensions
{
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
