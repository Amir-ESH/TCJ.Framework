using TCJ.Core.Guards;

namespace TCJ.Core.Extensions;

public static class CollectionExtensions
{
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
