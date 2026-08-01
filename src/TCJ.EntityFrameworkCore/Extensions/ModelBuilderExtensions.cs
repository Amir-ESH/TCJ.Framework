// ****************************************************************************************************************************************************
// File: ModelBuilderExtensions.cs
// Project: TCJ.Core
// Author: Amir Eslamzadeh
// Created: 1405-03-25 T23:03:32
// Modified: 1405-03-25 T23:03:32
// Version: 1.0.0
// ----------------------------------------------------------------------------------------------------------------------------------------------------
// Description:
//   Provides extension methods for Entity Framework Core ModelBuilder to
//   configure database schema conventions dynamically. Includes automatic
//   entity registration, table name pluralization, schema extraction from
//   module namespaces, and cascade delete behavior configuration.
// ----------------------------------------------------------------------------
// Dependencies:
//   - Microsoft.EntityFrameworkCore
//   - Pluralize.Core
//   - System.Reflection
// ****************************************************************************************************************************************************

using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Pluralize.Core;
using TCJ.Core.Guards;

namespace TCJ.EntityFrameworkCore.Extensions;

/// <summary>
/// Extension methods for <see cref="ModelBuilder"/> to apply conventions and configurations.
/// </summary>
public static class ModelBuilderExtensions
{
    private const string ModuleSuffix = "Module";
    private static readonly Pluralizer Pluralizer = new();

    /// <summary>
    /// Dynamically loads all <see cref="IEntityTypeConfiguration{TEntity}"/> implementations
    /// from the specified assemblies using reflection.
    /// </summary>
    /// <param name="modelBuilder">The model builder instance.</param>
    /// <param name="assemblies">Assemblies containing entity configurations.</param>
    public static void RegisterEntityTypeConfiguration(this ModelBuilder modelBuilder, params Assembly[] assemblies)
    {
        modelBuilder.NotNull(parameterName: nameof(modelBuilder));
        assemblies.NotNull(parameterName: nameof(assemblies));

        var applyGenericMethod = typeof(ModelBuilder).GetMethods()
            .First(methodInfo => methodInfo.Name == nameof(ModelBuilder.ApplyConfiguration));

        var configurationTypes = assemblies
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false, IsPublic: true }
                     && !type.GetCustomAttributes<OwnedAttribute>().Any());

        foreach (var type in configurationTypes)
        {
            foreach (var implementedInterface in type.GetInterfaces())
            {
                if (implementedInterface.IsConstructedGenericType
                 && implementedInterface.GetGenericTypeDefinition() == typeof(IEntityTypeConfiguration<>))
                {
                    var applyConcreteMethod = applyGenericMethod.MakeGenericMethod(implementedInterface.GenericTypeArguments[0]);
                    applyConcreteMethod.Invoke(modelBuilder, [Activator.CreateInstance(type)]);
                }
            }
        }
    }

    /// <summary>
    /// Sets <see cref="DeleteBehavior.Restrict"/> for all foreign key relationships
    /// that currently have cascade delete behavior.
    /// </summary>
    /// <param name="modelBuilder">The model builder instance.</param>
    public static void AddRestrictDeleteBehaviorConvention(this ModelBuilder modelBuilder)
    {
        modelBuilder.NotNull(parameterName: nameof(modelBuilder));

        var cascadeForeignKeys = modelBuilder.Model
            .GetEntityTypes()
            .SelectMany(mutableEntityType => mutableEntityType.GetForeignKeys())
            .Where(mutableForeignKey => mutableForeignKey is { IsOwnership: false, DeleteBehavior: DeleteBehavior.Cascade });

        foreach (var foreignKey in cascadeForeignKeys)
        {
            foreignKey.DeleteBehavior = DeleteBehavior.Restrict;
        }
    }

    /// <summary>
    /// Dynamically registers all entity types that inherit from the specified base type.
    /// Only includes types directly within ".Entities" namespace (not sub-namespaces).
    /// </summary>
    /// <typeparam name="TBaseType">The base type/interface that entities must implement.</typeparam>
    /// <param name="modelBuilder">The model builder instance.</param>
    /// <param name="assemblies">Assemblies containing entity types.</param>
    public static void RegisterAllEntities<TBaseType>(this ModelBuilder modelBuilder, params Assembly[] assemblies)
    {
        modelBuilder.NotNull(parameterName: nameof(modelBuilder));
        assemblies.NotNull(parameterName: nameof(assemblies));

        var baseType = typeof(TBaseType);
        var isGenericInterface = baseType is { IsGenericType: true, IsInterface: true };
        var genericTypeDefinition = isGenericInterface ? baseType.GetGenericTypeDefinition() : null;

        var entityTypes = assemblies
                          .SelectMany(assembly => assembly.GetExportedTypes())
                          .Where(type => type is { IsClass: true, IsAbstract: false, IsPublic: true }
                                   && IsInEntitiesNamespace(type.Namespace)
                                   && ImplementsBaseType(type, baseType, genericTypeDefinition)
                                   && !type.GetCustomAttributes<OwnedAttribute>().Any());

        foreach (var entityType in entityTypes)
        {
            modelBuilder.Entity(entityType);
        }
    }

    /// <summary>
    /// Checks if the namespace ends with ".Entities" (not a sub-namespace like ".Entities.JsonTypes").
    /// </summary>
    private static bool IsInEntitiesNamespace(string? namespaceName) // TODO: add generics and use with IEntity
    {
        return !string.IsNullOrEmpty(namespaceName)
            && (namespaceName.EndsWith(".Entities", StringComparison.Ordinal)
             || namespaceName.Contains(".Entities."));
    }

    /// <summary>
    /// Checks if a type implements the specified base type, including generic interfaces.
    /// </summary>
    private static bool ImplementsBaseType(Type type, Type baseType, Type? genericTypeDefinition)
    {
        if (genericTypeDefinition != null)
        {
            // For generic interfaces like IEntity<TKey>
            return type.GetInterfaces()
                       .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == genericTypeDefinition);
        }

        return baseType.IsAssignableFrom(type);
    }

    /// <summary>
    /// Applies pluralization to table names and sets schema based on module namespace.
    /// <para>
    /// Table naming: Post → Posts, Person → People
    /// </para>
    /// <para>
    /// Schema extraction: TCJ.AuthModule.Entities → Schema: Auth
    /// </para>
    /// </summary>
    /// <param name="modelBuilder">The model builder instance.</param>
    public static void AddPluralizingTableNameConvention(this ModelBuilder modelBuilder)
    {
        modelBuilder.NotNull(parameterName: nameof(modelBuilder));

        var entityTypes = modelBuilder.Model.GetEntityTypes();

        foreach (var entityType in entityTypes)
        {
            // Skip owned types - they don't have their own tables
            if (entityType.IsOwned())
            {
                continue;
            }

            // Pluralize table name
            var tableName = entityType.GetTableName();
            if (!string.IsNullOrWhiteSpace(tableName))
            {
                entityType.SetTableName(Pluralizer.Pluralize(tableName));
            }

            // Extract and set schema from module namespace
            var schema = ExtractSchemaFromNamespace(entityType.ClrType.Namespace);
            if (!string.IsNullOrWhiteSpace(schema))
            {
                entityType.SetSchema(schema);
            }
        }
    }

    /// <summary>
    /// Applies pluralization to table names and sets a custom schema for all entities.
    /// </summary>
    /// <param name="modelBuilder">The model builder instance.</param>
    /// <param name="defaultSchema">The default schema to apply when module schema cannot be extracted.</param>
    public static void AddPluralizingTableNameConvention(this ModelBuilder modelBuilder, string defaultSchema)
    {
        modelBuilder.NotNull(parameterName: nameof(modelBuilder));
        defaultSchema.NotNullOrWhiteSpace(parameterName: nameof(defaultSchema));

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.IsOwned())
            {
                continue;
            }

            var tableName = entityType.GetTableName();
            if (!string.IsNullOrWhiteSpace(tableName))
            {
                entityType.SetTableName(Pluralizer.Pluralize(tableName));
            }

            var schema = ExtractSchemaFromNamespace(entityType.ClrType.Namespace) ?? defaultSchema;
            entityType.SetSchema(schema);
        }
    }

    /// <summary>
    /// Extracts schema name from namespace based on module naming convention.
    /// </summary>
    /// <param name="namespaceName">The full namespace of the entity type.</param>
    /// <returns>
    /// The schema name extracted from the module part of the namespace,
    /// or <c>null</c> if no module pattern is found.
    /// </returns>
    /// <example>
    /// <code>
    /// ExtractSchemaFromNamespace("TCJ.AuthModule.Entities") // Returns: "Auth"
    /// ExtractSchemaFromNamespace("TCJ.AccountingModule.Domain") // Returns: "Accounting"
    /// ExtractSchemaFromNamespace("TCJ.Core.Entities") // Returns: null
    /// </code>
    /// </example>
    private static string? ExtractSchemaFromNamespace(string? namespaceName)
    {
        if (string.IsNullOrWhiteSpace(namespaceName))
        {
            return null;
        }

        var namespaceParts = namespaceName.Split('.');

        var modulePart = namespaceParts.FirstOrDefault(part =>
                                                           part.EndsWith(ModuleSuffix, StringComparison.OrdinalIgnoreCase));

        return modulePart?[..^ModuleSuffix.Length];
    }

    /// <summary>
    /// TODO: add summary
    /// </summary>
    /// <returns>Array of module assemblies.</returns>
    public static Assembly[] GetModuleAssemblies() // TODO fixed this
    {
        return AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(assembly => assembly.GetName().Name is { } name
                     && name.StartsWith("TCJ.", StringComparison.OrdinalIgnoreCase)
                     && !name.Contains("Empty", StringComparison.OrdinalIgnoreCase)
                     && (name.EndsWith(ModuleSuffix, StringComparison.OrdinalIgnoreCase)
                      || name.EndsWith(".Core", StringComparison.OrdinalIgnoreCase)
                      || name.EndsWith(".Email", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }
}
