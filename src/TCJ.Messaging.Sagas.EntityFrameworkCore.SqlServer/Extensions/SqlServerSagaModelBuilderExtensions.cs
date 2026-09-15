using Microsoft.EntityFrameworkCore;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Extensions;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Persistence;

namespace TCJ.Messaging.Sagas.EntityFrameworkCore.SqlServer.Extensions;

/// <summary>Applies SQL Server-specific durable Saga persistence conventions.</summary>
public static class SqlServerSagaModelBuilderExtensions
{
    /// <summary>
    /// Configures TCJ Saga entities, SQL Server rowversion optimistic concurrency, and the unique active-correlation constraint.
    /// Consumers remain responsible for generating and applying migrations.
    /// </summary>
    /// <param name="modelBuilder">Consumer application model builder.</param>
    /// <returns>The same model builder for chaining.</returns>
    public static ModelBuilder AddTcjSqlServerSagas(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.AddTcjSagas();

        modelBuilder.Entity<SagaInstance>().Property(x => x.ConcurrencyToken).IsRowVersion().IsConcurrencyToken();
        modelBuilder.Entity<SagaTimer>().Property(x => x.ConcurrencyToken).IsRowVersion().IsConcurrencyToken();
        modelBuilder.Entity<SagaCorrelation>()
            .HasIndex(x => new { x.SagaType, x.CorrelationName, x.ValueHash })
            .IsUnique()
            .HasFilter("[RemovedAtUtc] IS NULL")
            .HasDatabaseName("UX_TCJ_SagaCorrelations_Active");
        return modelBuilder;
    }
}
