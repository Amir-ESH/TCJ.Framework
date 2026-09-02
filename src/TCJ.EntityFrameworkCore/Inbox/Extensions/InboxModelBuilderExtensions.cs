using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TCJ.EntityFrameworkCore.Inbox.Extensions;

/// <summary>Configures the provider-independent transactional Inbox persistence model.</summary>
public static class InboxModelBuilderExtensions
{
    /// <summary>Adds the default <c>TCJ_InboxMessages</c> schema and required idempotency indexes.</summary>
    /// <param name="modelBuilder">Consumer-owned model builder.</param>
    /// <returns>The same model builder for chaining.</returns>
    public static ModelBuilder AddTcjInbox(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        EntityTypeBuilder<InboxMessage> builder = modelBuilder.Entity<InboxMessage>();
        builder.ToTable("TCJ_InboxMessages");
        builder.HasKey(message => message.Id).HasName("PK_TCJ_InboxMessages");
        builder.Property(message => message.Id).ValueGeneratedNever();
        builder.Property(message => message.MessageId).HasMaxLength(256).IsRequired();
        builder.Property(message => message.ConsumerName).HasMaxLength(128).IsRequired();
        builder.Property(message => message.MessageType).HasMaxLength(128).IsRequired();
        builder.Property(message => message.MessageVersion).IsRequired();
        builder.Property(message => message.PayloadHash).HasMaxLength(64).IsRequired();
        builder.Property(message => message.Payload);
        builder.Property(message => message.HeadersJson);
        builder.Property(message => message.ReceivedAtUtc).IsRequired();
        builder.Property(message => message.StartedAtUtc);
        builder.Property(message => message.ProcessedAtUtc);
        builder.Property(message => message.AttemptCount).IsRequired();
        builder.Property(message => message.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(message => message.LockId);
        builder.Property(message => message.LockedAtUtc);
        builder.Property(message => message.LockExpiresAtUtc);
        builder.Property(message => message.NextAttemptAtUtc);
        builder.Property(message => message.LastErrorType).HasMaxLength(64);
        builder.Property(message => message.LastError).HasMaxLength(4096);
        builder.Property(message => message.DeadLetteredAtUtc);
        builder.Property(message => message.CorrelationId).HasMaxLength(256);
        builder.Property(message => message.CausationId).HasMaxLength(256);
        builder.Property(message => message.CreatedAtUtc).IsRequired();
        builder.Property(message => message.UpdatedAtUtc).IsRequired();
        builder.Property(message => message.ReplayCount).IsRequired();
        builder.Property(message => message.LastReplayedAtUtc);

        builder.HasIndex(message => new { message.ConsumerName, message.MessageId })
            .IsUnique()
            .HasDatabaseName("UX_TCJ_InboxMessages_ConsumerName_MessageId");
        builder.HasIndex(message => new { message.Status, message.NextAttemptAtUtc })
            .HasDatabaseName("IX_TCJ_InboxMessages_Status_NextAttemptAtUtc");
        builder.HasIndex(message => message.LockExpiresAtUtc)
            .HasDatabaseName("IX_TCJ_InboxMessages_LockExpiresAtUtc");
        builder.HasIndex(message => message.ReceivedAtUtc)
            .HasDatabaseName("IX_TCJ_InboxMessages_ReceivedAtUtc");
        builder.HasIndex(message => message.ProcessedAtUtc)
            .HasDatabaseName("IX_TCJ_InboxMessages_ProcessedAtUtc");
        builder.HasIndex(message => message.MessageType)
            .HasDatabaseName("IX_TCJ_InboxMessages_MessageType");
        return modelBuilder;
    }
}
