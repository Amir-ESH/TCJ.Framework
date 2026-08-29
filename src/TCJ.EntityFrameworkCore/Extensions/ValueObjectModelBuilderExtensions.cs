using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TCJ.EntityFrameworkCore.StrongTypes;

namespace TCJ.EntityFrameworkCore.Extensions;

/// <summary>
/// Provides explicit model registration for generated primitive-backed Value Object conversions.
/// </summary>
public static class ValueObjectModelBuilderExtensions
{
    /// <summary>
    /// Applies explicitly registered Value Object conversions to every matching property in the EF Core model.
    /// </summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    /// <param name="registry">The explicit Value Object conversion registry.</param>
    /// <returns>The same model builder instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a matching property already has a different value converter.</exception>
    public static ModelBuilder ApplyValueObjectConversions(
        this ModelBuilder modelBuilder,
        ValueObjectConversionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(registry);

        foreach (IMutableEntityType entityType in modelBuilder.Model.GetEntityTypes().OrderBy(static entity => entity.Name, StringComparer.Ordinal))
        {
            foreach (IMutableProperty property in entityType.GetProperties().OrderBy(static property => property.Name, StringComparer.Ordinal))
            {
                Type propertyType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (!registry.TryGetRegistration(propertyType, out ValueObjectConversionRegistration registration))
                {
                    continue;
                }

                ValueConverter? existingConverter = property.GetValueConverter();
                if (existingConverter is not null && !ReferenceEquals(existingConverter, registration.Converter))
                {
                    throw new InvalidOperationException(
                        $"Property '{entityType.Name}.{property.Name}' already has a value converter that conflicts with the registered Value Object conversion for '{propertyType}'.");
                }

                property.SetValueConverter(registration.Converter);
            }
        }

        return modelBuilder;
    }
}
