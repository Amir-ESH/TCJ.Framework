namespace TCJ.Core.Extensions;
/// <summary>
/// Provides conditional enumerable query helpers.
/// </summary>

public static class EnumerableExtensions
{
    /// <summary>
    /// Applies the predicate only when the specified condition is true.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="source">The source collection or sequence.</param>
    /// <param name="condition">The condition value.</param>
    /// <param name="predicate">The predicate used to evaluate values.</param>
    /// <returns>The result of the operation.</returns>
    public static IEnumerable<T> WhereIf<T>(
        this IEnumerable<T> source,
        bool condition,
        Func<T, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(predicate);

        return condition ? source.Where(predicate) : source;
    }
    /// <summary>
    /// Applies the predicate only when the specified condition is true.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="source">The source collection or sequence.</param>
    /// <param name="condition">The condition value.</param>
    /// <param name="predicate">The predicate used to evaluate values.</param>
    /// <returns>The result of the operation.</returns>

    public static IEnumerable<T> WhereIf<T>(
        this IEnumerable<T> source,
        bool condition,
        Func<T, int, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(predicate);

        return condition ? source.Where(predicate) : source;
    }
}
