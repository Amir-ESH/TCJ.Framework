using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Persistence;

namespace TCJ.Messaging.Sagas.EntityFrameworkCore.Extensions;

/// <summary>Configures provider-neutral durable Saga persistence in a consumer-owned DbContext.</summary>
public static class SagaModelBuilderExtensions
{
    /// <summary>
    /// Adds framework-owned Saga instance, correlation, and timer tables. Consumers remain responsible
    /// for generating, reviewing, and applying their own EF Core migrations.
    /// </summary>
    /// <param name="modelBuilder">Consumer application model builder.</param>
    /// <returns>The same model builder for chaining.</returns>
    public static ModelBuilder AddTcjSagas(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        EntityTypeBuilder<SagaInstance> saga = modelBuilder.Entity<SagaInstance>();
        saga.ToTable("TCJ_SagaInstances");
        saga.HasKey(x => x.SagaId).HasName("PK_TCJ_SagaInstances");
        saga.Property(x => x.SagaId).ValueGeneratedNever();
        saga.Property(x => x.SagaType).HasMaxLength(128).IsRequired();
        saga.Property(x => x.DefinitionVersion).IsRequired();
        saga.Property(x => x.StateSchemaVersion).IsRequired();
        saga.Property(x => x.CurrentState).HasMaxLength(64).IsRequired();
        saga.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        saga.Property(x => x.StatePayload).IsRequired();
        saga.Property(x => x.CreatedAtUtc).IsRequired();
        saga.Property(x => x.UpdatedAtUtc).IsRequired();
        saga.Property(x => x.CompletedAtUtc);
        saga.Property(x => x.FailedAtUtc);
        saga.Property(x => x.CompensationStatus).HasConversion<string>().HasMaxLength(32).IsRequired();
        saga.Property(x => x.CompensationAttemptCount).IsRequired();
        saga.Property(x => x.NextCompensationAttemptAtUtc);
        saga.Property(x => x.LastCompensationFailureType).HasMaxLength(128);
        saga.Ignore(x => x.DomainEvents);
        saga.HasIndex(x => new { x.SagaType, x.Status }).HasDatabaseName("IX_TCJ_SagaInstances_SagaType_Status");
        saga.HasIndex(x => x.UpdatedAtUtc).HasDatabaseName("IX_TCJ_SagaInstances_UpdatedAtUtc");

        EntityTypeBuilder<SagaCorrelation> correlation = modelBuilder.Entity<SagaCorrelation>();
        correlation.ToTable("TCJ_SagaCorrelations");
        correlation.HasKey(x => x.Id).HasName("PK_TCJ_SagaCorrelations");
        correlation.Property(x => x.Id).ValueGeneratedNever();
        correlation.Property(x => x.SagaId).IsRequired();
        correlation.Property(x => x.SagaType).HasMaxLength(128).IsRequired();
        correlation.Property(x => x.CorrelationName).HasMaxLength(64).IsRequired();
        correlation.Property(x => x.ValueHash).HasMaxLength(64).IsRequired();
        correlation.Property(x => x.CreatedAtUtc).IsRequired();
        correlation.Property(x => x.RemovedAtUtc);
        correlation.HasIndex(x => new { x.SagaType, x.CorrelationName, x.ValueHash })
            .HasDatabaseName("IX_TCJ_SagaCorrelations_Lookup");
        correlation.HasIndex(x => x.SagaId).HasDatabaseName("IX_TCJ_SagaCorrelations_SagaId");

        // Correlation reservation is inserted provider-side before the new SagaInstance is flushed so
        // concurrent starts are serialized by the database unique constraint. Keep this association
        // intentionally non-FK; cleanup removes correlations in the same transaction as the
        // rowversion-protected SagaInstance delete and rolls them back if the delete loses a race.
        EntityTypeBuilder<SagaTimer> timer = modelBuilder.Entity<SagaTimer>();
        timer.ToTable("TCJ_SagaTimers");
        timer.HasKey(x => x.TimerId).HasName("PK_TCJ_SagaTimers");
        timer.Property(x => x.TimerId).ValueGeneratedNever();
        timer.Property(x => x.SagaId).IsRequired();
        timer.Property(x => x.SagaType).HasMaxLength(128).IsRequired();
        timer.Property(x => x.TimerName).HasMaxLength(128).IsRequired();
        timer.Property(x => x.TimeoutType).HasMaxLength(128).IsRequired();
        timer.Property(x => x.DueAtUtc).IsRequired();
        timer.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        timer.Property(x => x.AttemptCount).IsRequired();
        timer.Property(x => x.LockId);
        timer.Property(x => x.LockedAtUtc);
        timer.Property(x => x.LockExpiresAtUtc);
        timer.Property(x => x.CompletedAtUtc);
        timer.Property(x => x.CreatedAtUtc).IsRequired();
        timer.Property(x => x.UpdatedAtUtc).IsRequired();
        timer.Property(x => x.LastFailureType).HasMaxLength(128);
        timer.HasIndex(x => new { x.Status, x.DueAtUtc }).HasDatabaseName("IX_TCJ_SagaTimers_Status_DueAtUtc");
        timer.HasIndex(x => x.LockExpiresAtUtc).HasDatabaseName("IX_TCJ_SagaTimers_LockExpiresAtUtc");
        timer.HasIndex(x => new { x.SagaId, x.TimerName }).IsUnique().HasDatabaseName("UX_TCJ_SagaTimers_SagaId_TimerName");
        timer.HasOne<SagaInstance>()
            .WithMany()
            .HasForeignKey(x => x.SagaId)
            .OnDelete(DeleteBehavior.Cascade);
        return modelBuilder;
    }
}
