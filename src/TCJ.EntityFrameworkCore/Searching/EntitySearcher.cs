using System.Collections.Concurrent;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Searching.Internal;

namespace TCJ.EntityFrameworkCore.Searching;

/// <summary>
/// Searches only entity types and scalar properties that are present in the finalized
/// Entity Framework Core model.
/// </summary>
public sealed class EntitySearcher : IEntitySearcher
{
    private static readonly ConcurrentDictionary<Type, IEntitySearchExecutor> Executors = new();

    private readonly IReadDbContext _readDb;
    private readonly IReadOnlyList<IEntityType> _entityTypes;

    /// <summary>
    /// Initializes a new entity searcher for the current DbContext model.
    /// </summary>
    public EntitySearcher(IReadDbContext readDb)
    {
        ArgumentNullException.ThrowIfNull(readDb);

        _readDb = readDb;
        _entityTypes = readDb.Model
            .GetEntityTypes()
            .Where(static entityType => entityType.FindOwnership() is null)
            .ToArray();
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(
        EntityRecordInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        IEntityType entityType = ResolveEntityType(input.EntityName);
        LambdaExpression predicate = CreatePrimaryKeyPredicate(entityType, input.KeyValues);
        IEntitySearchExecutor executor = GetExecutor(entityType.ClrType);

        return executor.ExistsAsync(_readDb, predicate, cancellationToken);
    }

    /// <inheritdoc />
    public Task<object?> FindAsync(
        EntityRecordInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        IEntityType entityType = ResolveEntityType(input.EntityName);
        LambdaExpression predicate = CreatePrimaryKeyPredicate(entityType, input.KeyValues);
        IEntitySearchExecutor executor = GetExecutor(entityType.ClrType);

        return executor.FindAsync(_readDb, predicate, cancellationToken);
    }

    /// <inheritdoc />
    public EntityPropertyMetadata GetPropertyMetadata(EntityPropertyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        IEntityType entityType = ResolveEntityType(input.EntityName);
        IProperty property = ResolveProperty(entityType, input.PropertyName);
        IKey? primaryKey = entityType.FindPrimaryKey();

        return new EntityPropertyMetadata(
            EntityName: entityType.ClrType.FullName ?? entityType.Name,
            PropertyName: property.Name,
            ClrTypeName: property.ClrType.FullName ?? property.ClrType.Name,
            IsNullable: property.IsNullable,
            IsPrimaryKey: primaryKey?.Properties.Contains(property) == true,
            IsShadowProperty: property.IsShadowProperty(),
            IsConcurrencyToken: property.IsConcurrencyToken);
    }

    private IEntityType ResolveEntityType(string entityName)
    {
        IEntityType[] fullNameMatches = _entityTypes
            .Where(entityType =>
                string.Equals(entityType.Name, entityName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entityType.ClrType.FullName, entityName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (fullNameMatches.Length == 1)
        {
            return fullNameMatches[0];
        }

        IEntityType[] shortNameMatches = _entityTypes
            .Where(entityType =>
                string.Equals(entityType.ClrType.Name, entityName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (shortNameMatches.Length == 1)
        {
            return shortNameMatches[0];
        }

        if (fullNameMatches.Length > 1 || shortNameMatches.Length > 1)
        {
            IEnumerable<string> matches = fullNameMatches
                .Concat(shortNameMatches)
                .Select(entityType => entityType.ClrType.FullName ?? entityType.Name)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);

            throw new InvalidOperationException(
                $"Entity name '{entityName}' is ambiguous. Use one of these full names: " +
                string.Join(", ", matches));
        }

        throw new InvalidOperationException(
            $"Entity '{entityName}' is not mapped in the current Entity Framework Core model.");
    }

    private static IProperty ResolveProperty(IEntityType entityType, string propertyName)
    {
        IProperty[] matches = entityType
            .GetProperties()
            .Where(property => string.Equals(
                property.Name,
                propertyName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException(
                $"Scalar property '{propertyName}' is not mapped for entity " +
                $"'{entityType.ClrType.FullName ?? entityType.Name}'."),
            _ => throw new InvalidOperationException(
                $"Scalar property name '{propertyName}' is ambiguous for entity " +
                $"'{entityType.ClrType.FullName ?? entityType.Name}'."),
        };
    }

    private static LambdaExpression CreatePrimaryKeyPredicate(
        IEntityType entityType,
        IReadOnlyDictionary<string, string> suppliedValues)
    {
        IKey primaryKey = entityType.FindPrimaryKey()
            ?? throw new InvalidOperationException(
                $"Entity '{entityType.ClrType.FullName ?? entityType.Name}' does not have a primary key.");

        HashSet<string> primaryKeyNames = primaryKey.Properties
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string[] missingProperties = primaryKeyNames
            .Where(propertyName => !suppliedValues.ContainsKey(propertyName))
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] unexpectedProperties = suppliedValues.Keys
            .Where(propertyName => !primaryKeyNames.Contains(propertyName))
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (missingProperties.Length > 0 || unexpectedProperties.Length > 0)
        {
            string expectedProperties = string.Join(", ", primaryKey.Properties.Select(static property => property.Name));
            string suppliedProperties = string.Join(", ", suppliedValues.Keys.Order(StringComparer.Ordinal));

            throw new ArgumentException(
                $"Primary-key values for entity '{entityType.ClrType.FullName ?? entityType.Name}' " +
                $"do not match its mapped key. Expected: [{expectedProperties}]. " +
                $"Supplied: [{suppliedProperties}].",
                nameof(suppliedValues));
        }

        ParameterExpression parameter = Expression.Parameter(entityType.ClrType, "entity");
        Expression? body = null;

        foreach (IProperty keyProperty in primaryKey.Properties)
        {
            string suppliedValue = suppliedValues[keyProperty.Name];
            object convertedValue = EntityKeyValueConverter.ConvertFromInvariantString(
                suppliedValue,
                keyProperty.ClrType,
                entityType.ClrType.FullName ?? entityType.Name,
                keyProperty.Name);

            MethodCallExpression propertyAccess = Expression.Call(
                typeof(EF),
                nameof(EF.Property),
                [keyProperty.ClrType],
                Expression.Convert(parameter, typeof(object)),
                Expression.Constant(keyProperty.Name));

            BinaryExpression equality = Expression.Equal(
                propertyAccess,
                Expression.Constant(convertedValue, keyProperty.ClrType));

            body = body is null ? equality : Expression.AndAlso(body, equality);
        }

        Type predicateType = typeof(Func<,>).MakeGenericType(entityType.ClrType, typeof(bool));
        return Expression.Lambda(predicateType, body!, parameter);
    }

    private static IEntitySearchExecutor GetExecutor(Type entityType)
    {
        return Executors.GetOrAdd(
            entityType,
            static type =>
            {
                Type executorType = typeof(EntitySearchExecutor<>).MakeGenericType(type);

                return (IEntitySearchExecutor?)Activator.CreateInstance(executorType)
                    ?? throw new InvalidOperationException(
                        $"Could not create an entity-search executor for '{type.FullName}'.");
            });
    }
}
