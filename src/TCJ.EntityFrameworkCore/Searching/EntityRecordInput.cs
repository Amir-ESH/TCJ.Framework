using System.Collections.ObjectModel;

namespace TCJ.EntityFrameworkCore.Searching;

/// <summary>
/// Identifies an entity record by the entity name and all of its primary-key values.
/// </summary>
public sealed record EntityRecordInput
{
    /// <summary>
    /// Initializes a new entity-record lookup.
    /// </summary>
    /// <param name="entityName">
    /// The mapped CLR full name or an unambiguous CLR short name.
    /// </param>
    /// <param name="keyValues">
    /// Primary-key property names and their invariant string representations.
    /// Composite keys must provide every primary-key property.
    /// </param>
    public EntityRecordInput(string entityName, IReadOnlyDictionary<string, string> keyValues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentNullException.ThrowIfNull(keyValues);

        if (keyValues.Count == 0)
        {
            throw new ArgumentException("At least one primary-key value is required.", nameof(keyValues));
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string> keyValue in keyValues)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyValue.Key);
            ArgumentNullException.ThrowIfNull(keyValue.Value);

            if (!values.TryAdd(keyValue.Key.Trim(), keyValue.Value))
            {
                throw new ArgumentException(
                    $"The primary-key property '{keyValue.Key}' was supplied more than once.",
                    nameof(keyValues));
            }
        }

        EntityName = entityName.Trim();
        KeyValues = new ReadOnlyDictionary<string, string>(values);
    }

    /// <summary>
    /// Gets the mapped entity name.
    /// </summary>
    public string EntityName { get; }

    /// <summary>
    /// Gets the supplied primary-key values, using case-insensitive property names.
    /// </summary>
    public IReadOnlyDictionary<string, string> KeyValues { get; }

    /// <summary>
    /// Creates an input for an entity with a single-column primary key.
    /// </summary>
    /// <param name="entityName">The entity name.</param>
    /// <param name="keyPropertyName">The key property name.</param>
    /// <param name="keyValue">The key value.</param>
    /// <returns>The result of the operation.</returns>
    public static EntityRecordInput ForSingleKey(
        string entityName,
        string keyPropertyName,
        string keyValue)
    {
        return new EntityRecordInput(
            entityName,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [keyPropertyName] = keyValue,
            });
    }
}
