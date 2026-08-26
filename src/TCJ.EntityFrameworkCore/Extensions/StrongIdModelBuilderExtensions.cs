using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TCJ.EntityFrameworkCore.StrongTypes;

namespace TCJ.EntityFrameworkCore.Extensions;

/// <summary>
/// Provides explicit model registration for generated strongly typed identifier conversions.
/// </summary>
public static class StrongIdModelBuilderExtensions
{
    /// <summary>
    /// Applies explicitly registered Strong ID conversions to every matching property in the EF Core model.
    /// </summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    /// <param name="registry">The explicit Strong ID conversion registry.</param>
    /// <returns>The same model builder instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a matching property already has a different value converter.</exception>
    public static ModelBuilder ApplyStrongIdConversions(
        this ModelBuilder modelBuilder,
        StrongIdConversionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(registry);

        foreach (IMutableEntityType entityType in modelBuilder.Model.GetEntityTypes().OrderBy(static entity => entity.Name, StringComparer.Ordinal))
        {
            foreach (IMutableProperty property in entityType.GetProperties().OrderBy(static property => property.Name, StringComparer.Ordinal))
            {
                Type propertyType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (!registry.TryGetRegistration(propertyType, out StrongIdConversionRegistration registration))
                {
                    continue;
                }

                ValueConverter? existingConverter = property.GetValueConverter();
                if (existingConverter is not null && !ReferenceEquals(existingConverter, registration.Converter))
                {
                    throw new InvalidOperationException(
                        $"Property '{entityType.Name}.{property.Name}' already has a value converter that conflicts with the registered Strong ID conversion for '{propertyType}'.");
                }

                property.SetValueConverter(registration.Converter);
            }
        }

        return modelBuilder;
    }
}
