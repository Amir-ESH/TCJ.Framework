using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace TCJ.EntityFrameworkCore.StrongTypes;

/// <summary>
/// Stores explicit provider-neutral EF Core conversions for generated strongly typed identifiers.
/// </summary>
public sealed class StrongIdConversionRegistry
{
    private readonly Dictionary<Type, StrongIdConversionRegistration> _registrations = [];

    /// <summary>
    /// Initializes an empty Strong ID conversion registry.
    /// </summary>
    public StrongIdConversionRegistry()
    {
    }

    /// <summary>
    /// Registers a generated strongly typed identifier and its primitive backing type.
    /// </summary>
    /// <typeparam name="TStrongId">The generated strongly typed identifier type.</typeparam>
    /// <typeparam name="TBacking">The primitive provider type. Supported types are <see cref="Guid"/>, <see cref="int"/>, and <see cref="long"/>.</typeparam>
    /// <param name="toBackingValue">The generated expression that extracts the backing value.</param>
    /// <param name="fromBackingValue">The generated expression that reconstructs the strongly typed identifier.</param>
    /// <returns>The current registry so registrations can be chained.</returns>
    /// <exception cref="NotSupportedException">Thrown when <typeparamref name="TBacking"/> is not a supported Strong ID backing type.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the Strong ID type has already been registered with a conflicting backing type or conversion expressions.</exception>
    public StrongIdConversionRegistry Register<TStrongId, TBacking>(
        Expression<Func<TStrongId, TBacking>> toBackingValue,
        Expression<Func<TBacking, TStrongId>> fromBackingValue)
        where TStrongId : struct
        where TBacking : struct
    {
        ArgumentNullException.ThrowIfNull(toBackingValue);
        ArgumentNullException.ThrowIfNull(fromBackingValue);

        Type strongIdType = typeof(TStrongId);
        Type backingType = typeof(TBacking);
        ValidateBackingType(strongIdType, backingType);

        if (_registrations.TryGetValue(strongIdType, out StrongIdConversionRegistration? existing))
        {
            if (existing.BackingType != backingType)
            {
                throw new InvalidOperationException(
                    $"Strong ID '{strongIdType}' is already registered with backing type '{existing.BackingType}' and cannot also be registered with '{backingType}'.");
            }

            if (ReferenceEquals(existing.ToBackingValue, toBackingValue)
                && ReferenceEquals(existing.FromBackingValue, fromBackingValue))
            {
                return this;
            }

            throw new InvalidOperationException(
                $"Strong ID '{strongIdType}' is already registered with different conversion expressions for backing type '{backingType}'.");
        }

        var converter = new ValueConverter<TStrongId, TBacking>(toBackingValue, fromBackingValue);
        _registrations.Add(
            strongIdType,
            new StrongIdConversionRegistration(
                backingType,
                toBackingValue,
                fromBackingValue,
                converter));

        return this;
    }

    internal bool TryGetRegistration(Type strongIdType, out StrongIdConversionRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(strongIdType);
        return _registrations.TryGetValue(strongIdType, out registration!);
    }

    private static void ValidateBackingType(Type strongIdType, Type backingType)
    {
        if (backingType != typeof(Guid) && backingType != typeof(int) && backingType != typeof(long))
        {
            throw new NotSupportedException(
                $"Strong ID '{strongIdType}' uses unsupported backing type '{backingType}'. Supported backing types are Guid, Int32, and Int64.");
        }
    }
}
