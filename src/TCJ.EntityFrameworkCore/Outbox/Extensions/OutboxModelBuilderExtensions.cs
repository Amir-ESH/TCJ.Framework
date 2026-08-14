using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TCJ.EntityFrameworkCore.Outbox.Extensions;

/// <summary>Configures the provider-independent transactional-outbox persistence model.</summary>
public static class OutboxModelBuilderExtensions
{
    /// <summary>
    /// Adds the TCJ outbox table, primary key, required columns, and processing indexes.
    /// Consumers remain responsible for generating and applying their own migrations.
    /// </summary>
    /// <param name="modelBuilder">Model builder for the consumer-owned <see cref="DbContext"/>.</param>
    /// <returns>The same model builder for chaining.</returns>
    public static ModelBuilder AddTcjOutbox(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        EntityTypeBuilder<OutboxMessage> builder = modelBuilder.Entity<OutboxMessage>();
        builder.ToTable("TCJ_OutboxMessages");
        builder.HasKey(message => message.Id).HasName("PK_TCJ_OutboxMessages");

        builder.Property(message => message.Id).ValueGeneratedNever();
        builder.Property(message => message.OccurredAtUtc).IsRequired();
        builder.Property(message => message.EventType).HasMaxLength(128).IsRequired();
        builder.Property(message => message.Payload).IsRequired();
        builder.Property(message => message.AttemptCount).IsRequired();
        builder.Property(message => message.NextAttemptAtUtc).IsRequired();
        builder.Property(message => message.LockedAtUtc);
        builder.Property(message => message.LockExpiresAtUtc);
        builder.Property(message => message.LockId);
        builder.Property(message => message.ProcessedAtUtc);
        builder.Property(message => message.DeadLetteredAtUtc);
        builder.Property(message => message.LastErrorType).HasMaxLength(256);
        builder.Property(message => message.LastError).HasMaxLength(4096);
        builder.Property(message => message.CreatedAtUtc).IsRequired();
        builder.Property(message => message.UpdatedAtUtc).IsRequired();
        builder.Property(message => message.ReplayCount).IsRequired();
        builder.Property(message => message.LastReplayedAtUtc);

        builder.HasIndex(message => new { message.ProcessedAtUtc, message.NextAttemptAtUtc })
            .HasDatabaseName("IX_TCJ_OutboxMessages_ProcessedAtUtc_NextAttemptAtUtc");
        builder.HasIndex(message => message.LockExpiresAtUtc)
            .HasDatabaseName("IX_TCJ_OutboxMessages_LockExpiresAtUtc");
        builder.HasIndex(message => message.OccurredAtUtc)
            .HasDatabaseName("IX_TCJ_OutboxMessages_OccurredAtUtc");
        builder.HasIndex(message => message.EventType)
            .HasDatabaseName("IX_TCJ_OutboxMessages_EventType");

        return modelBuilder;
    }
}
