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
    /// <param name="modelBuilder">The EF Core model builder to configure.</param>
    /// <returns>The result of the operation.</returns>
    public static ModelBuilder ApplyTcjSqlServerConventions(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        return modelBuilder.ConfigureRowVersionProperties();
    }

    /// <summary>
    /// Configures properties exposed by <see cref="IRowVersion"/> as required,
    /// database-generated SQL Server <c>rowversion</c> concurrency tokens.
    /// </summary>
    /// <param name="modelBuilder">The EF Core model builder to configure.</param>
    /// <returns>The result of the operation.</returns>
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

            property.IsNullable = false;
            property.IsConcurrencyToken = true;
            property.ValueGenerated = ValueGenerated.OnAddOrUpdate;
            property.SetColumnType("rowversion");
        }

        return modelBuilder;
    }
}
