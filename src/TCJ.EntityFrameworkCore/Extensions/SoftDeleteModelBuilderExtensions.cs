using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TCJ.Core.Entities;

namespace TCJ.EntityFrameworkCore.Extensions;

/// <summary>
/// Configures global query filters for entities that support logical deletion.
/// </summary>
public static class SoftDeleteModelBuilderExtensions
{
    private const string AppliedAnnotationName = "TCJ:SoftDeleteQueryFiltersApplied";

    /// <summary>
    /// Adds a global <c>IsDeleted == false</c> query filter to each root entity type
    /// that implements <see cref="ISoftDelete"/>. Existing query filters are preserved
    /// and combined with the soft-delete predicate.
    /// </summary>
    public static ModelBuilder ApplySoftDeleteQueryFilters(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        if (Equals(modelBuilder.Model.FindAnnotation(AppliedAnnotationName)?.Value, true))
        {
            return modelBuilder;
        }

        IMutableEntityType[] softDeleteEntityTypes = modelBuilder.Model
                                                                 .GetEntityTypes()
                                                                 .Where(entityType => typeof(ISoftDelete).IsAssignableFrom(entityType.ClrType))
                                                                 .ToArray();

        foreach (IMutableEntityType entityType in softDeleteEntityTypes)
        {
            if (entityType.IsOwned())
            {
                continue;
            }

            if (entityType.BaseType is not null)
            {
                IMutableEntityType? rootEntityType = entityType;

                while (rootEntityType.BaseType is not null)
                {
                    rootEntityType = rootEntityType.BaseType;
                }

                if (!typeof(ISoftDelete).IsAssignableFrom(rootEntityType.ClrType))
                {
                    throw new InvalidOperationException($"Soft-delete entity '{entityType.ClrType.FullName}' is derived from " +
                                                        $"'{rootEntityType.ClrType.FullName}', which does not implement " +
                                                        $"'{nameof(ISoftDelete)}'. EF Core query filters can only be " +
                                                        "configured on the root entity type.");
                }

                continue;
            }

            ApplyFilter(modelBuilder, entityType.ClrType, entityType.GetQueryFilter());
        }

        modelBuilder.Model.SetAnnotation(AppliedAnnotationName, true);
        return modelBuilder;
    }

    private static void ApplyFilter(ModelBuilder modelBuilder, Type entityType, LambdaExpression? existingFilter)
    {
        ParameterExpression parameter = Expression.Parameter(entityType, "entity");

        MethodCallExpression isDeletedProperty = Expression.Call(typeof(EF),
                                                                 nameof(EF.Property),
                                                                 [typeof(bool)],
                                                                 parameter,
                                                                 Expression.Constant(nameof(ISoftDelete.IsDeleted)));

        Expression filterBody = Expression.Not(isDeletedProperty);

        if (existingFilter is not null)
        {
            Expression existingBody = new ParameterReplacingExpressionVisitor(source: existingFilter.Parameters[0], target: parameter)
                                          .Visit(existingFilter.Body)
                                   ?? throw new InvalidOperationException($"The existing query filter for '{entityType.FullName}' could not be composed.");

            filterBody = Expression.AndAlso(existingBody, filterBody);
        }

        LambdaExpression filter = Expression.Lambda(filterBody, parameter);

        modelBuilder.Entity(entityType).HasQueryFilter(filter);
    }

    private sealed class ParameterReplacingExpressionVisitor(ParameterExpression source, ParameterExpression target)
        : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
            => node == source ? target : base.VisitParameter(node);
    }
}
