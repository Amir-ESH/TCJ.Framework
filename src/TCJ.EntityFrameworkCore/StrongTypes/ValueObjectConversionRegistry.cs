using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace TCJ.EntityFrameworkCore.StrongTypes;

/// <summary>
/// Stores explicit provider-neutral EF Core conversions for generated primitive-backed Value Objects.
/// </summary>
public sealed class ValueObjectConversionRegistry
{
    private readonly Dictionary<Type, ValueObjectConversionRegistration> _registrations = [];

    /// <summary>
    /// Initializes an empty Value Object conversion registry.
    /// </summary>
    public ValueObjectConversionRegistry()
    {
    }

    /// <summary>
    /// Registers a generated Value Object and its primitive backing type.
    /// </summary>
    /// <typeparam name="TValueObject">The generated Value Object type.</typeparam>
    /// <typeparam name="TBacking">The primitive provider type. Supported types are <see cref="string"/>, <see cref="Guid"/>, <see cref="int"/>, <see cref="long"/>, and <see cref="decimal"/>.</typeparam>
    /// <param name="toBackingValue">The generated expression that extracts the backing value.</param>
    /// <param name="fromBackingValue">The generated expression that validates and reconstructs the Value Object.</param>
    /// <returns>The current registry so registrations can be chained.</returns>
    /// <exception cref="NotSupportedException">Thrown when <typeparamref name="TBacking"/> is not a supported Value Object backing type.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the Value Object type has already been registered with a conflicting backing type or conversion expressions.</exception>
    public ValueObjectConversionRegistry Register<TValueObject, TBacking>(
        Expression<Func<TValueObject, TBacking>> toBackingValue,
        Expression<Func<TBacking, TValueObject>> fromBackingValue)
        where TValueObject : struct
    {
        ArgumentNullException.ThrowIfNull(toBackingValue);
        ArgumentNullException.ThrowIfNull(fromBackingValue);

        Type valueObjectType = typeof(TValueObject);
        Type backingType = typeof(TBacking);
        ValidateBackingType(valueObjectType, backingType);

        if (_registrations.TryGetValue(valueObjectType, out ValueObjectConversionRegistration? existing))
        {
            if (existing.BackingType != backingType)
            {
                throw new InvalidOperationException(
                    $"Value Object '{valueObjectType}' is already registered with backing type '{existing.BackingType}' and cannot also be registered with '{backingType}'.");
            }

            if (ReferenceEquals(existing.ToBackingValue, toBackingValue)
                && ReferenceEquals(existing.FromBackingValue, fromBackingValue))
            {
                return this;
            }

            throw new InvalidOperationException(
                $"Value Object '{valueObjectType}' is already registered with different conversion expressions for backing type '{backingType}'.");
        }

        var converter = new ValueConverter<TValueObject, TBacking>(toBackingValue, fromBackingValue);
        _registrations.Add(
            valueObjectType,
            new ValueObjectConversionRegistration(
                backingType,
                toBackingValue,
                fromBackingValue,
                converter));

        return this;
    }

    internal bool TryGetRegistration(Type valueObjectType, out ValueObjectConversionRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(valueObjectType);
        return _registrations.TryGetValue(valueObjectType, out registration!);
    }

    private static void ValidateBackingType(Type valueObjectType, Type backingType)
    {
        if (backingType != typeof(string)
            && backingType != typeof(Guid)
            && backingType != typeof(int)
            && backingType != typeof(long)
            && backingType != typeof(decimal))
        {
            throw new NotSupportedException(
                $"Value Object '{valueObjectType}' uses unsupported backing type '{backingType}'. Supported backing types are String, Guid, Int32, Int64, and Decimal.");
        }
    }
}
