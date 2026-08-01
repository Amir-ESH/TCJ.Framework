using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TCJ.Core.Entities;

namespace TCJ.EntityFrameworkCore.SqlServer.Extensions;

/// <summary>
/// Applies SQL Server-specific conventions to an Entity Framework Core model.
/// </summary>
public static class SqlServerModelBuilderExtensions
{
    /// <summary>
    /// Applies all TCJ SQL Server model conventions.
    /// </summary>
    public static ModelBuilder ApplyTcjSqlServerConventions(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        return modelBuilder.ConfigureRowVersionProperties();
    }

    /// <summary>
    /// Configures properties exposed by <see cref="IRowVersion"/> as required,
    /// database-generated SQL Server <c>rowversion</c> concurrency tokens.
    /// </summary>
    public static ModelBuilder ConfigureRowVersionProperties(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var configuredProperties = new HashSet<IMutableProperty>();

        foreach (IMutableEntityType entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(IRowVersion).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            IMutableProperty property = entityType.FindProperty(nameof(IRowVersion.RowVersion))
                                     ?? throw new InvalidOperationException($"Entity '{entityType.ClrType.FullName}' implements {nameof(IRowVersion)}, " +
                                                                            $"but property '{nameof(IRowVersion.RowVersion)}' is not mapped.");

            if (property.ClrType != typeof(byte[]))
            {
                throw new InvalidOperationException(
                    $"Property '{entityType.ClrType.FullName}.{property.Name}' must have CLR type byte[].");
            }

            if (!configuredProperties.Add(property))
            {
                continue;
            }

            modelBuilder.Entity(property.DeclaringType.ClrType)
                        .Property<byte[]>(property.Name)
                        .IsRequired()
                        .IsRowVersion()
                        .HasColumnType("rowversion");
        }

        return modelBuilder;
    }
}
