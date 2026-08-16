using TCJ.Core.Guards;

namespace TCJ.Core.Extensions;
/// <summary>
/// Provides collection-oriented extension methods.
/// </summary>

public static class CollectionExtensions
{
    /// <summary>
    /// Adds an item when the collection does not already contain the requested value or match.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="source">The source collection or sequence.</param>
    /// <param name="item">The item to process.</param>
    /// <returns>The result of the operation.</returns>
    public static bool AddIfNotContains<T>(this ICollection<T> source, T item)
    {
        source.NotNull();

        if (source.Contains(item))
        {
            return false;
        }

        source.Add(item);
        return true;
    }
    /// <summary>
    /// Adds an item when the collection does not already contain the requested value or match.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="source">The source collection or sequence.</param>
    /// <param name="items">The items to process.</param>
    /// <returns>The result of the operation.</returns>

    public static IReadOnlyList<T> AddIfNotContains<T>(
        this ICollection<T> source,
        IEnumerable<T> items)
    {
        source.NotNull();
        items.NotNull();

        var addedItems = new List<T>();

        foreach (var item in items)
        {
            if (source.Contains(item))
            {
                continue;
            }

            source.Add(item);
            addedItems.Add(item);
        }

        return addedItems;
    }
    /// <summary>
    /// Adds an item when the collection does not already contain the requested value or match.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="source">The source collection or sequence.</param>
    /// <param name="predicate">The predicate used to evaluate values.</param>
    /// <param name="itemFactory">The factory used to create an item when one is required.</param>
    /// <returns>The result of the operation.</returns>

    public static bool AddIfNotContains<T>(
        this ICollection<T> source,
        Func<T, bool> predicate,
        Func<T> itemFactory)
    {
        source.NotNull();
        predicate.NotNull();
        itemFactory.NotNull();

        if (source.Any(predicate))
        {
            return false;
        }

        source.Add(itemFactory());
        return true;
    }
    /// <summary>
    /// Removes items that satisfy the specified predicate.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="source">The source collection or sequence.</param>
    /// <param name="predicate">The predicate used to evaluate values.</param>
    /// <returns>The resulting value.</returns>

    public static IReadOnlyList<T> RemoveWhere<T>(
        this ICollection<T> source,
        Func<T, bool> predicate)
    {
        source.NotNull();
        predicate.NotNull();

        var removedItems = source.Where(predicate).ToArray();

        foreach (var item in removedItems)
        {
            source.Remove(item);
        }

        return removedItems;
    }
    /// <summary>
    /// Removes the specified items from the collection.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="source">The source collection or sequence.</param>
    /// <param name="items">The items to process.</param>

    public static void RemoveRange<T>(
        this ICollection<T> source,
        IEnumerable<T> items)
    {
        source.NotNull();
        items.NotNull();

        foreach (var item in items)
        {
            source.Remove(item);
        }
    }
}
