using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TCJ.Core.Inbox;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Inbox.Serialization;
using TCJ.EntityFrameworkCore.Inbox.Storage;

namespace TCJ.EntityFrameworkCore.Inbox.Processing;

internal sealed class InboxStartupValidator<TDbContext> : IInboxStartupValidator
    where TDbContext : DbContext, IReadDbContext, IWriteDbContext
{
    private readonly TDbContext _dbContext;
    private readonly IInboxStorage _storage;
    private readonly IReadOnlyList<IInboxStorage> _storages;
    private readonly InboxMessageRegistry _registry;
    private readonly IInboxSerializer _serializer;
    private readonly TcjInboxOptions _options;
    private bool _validated;

    public InboxStartupValidator(TDbContext dbContext, IInboxStorage storage, IEnumerable<IInboxStorage> storages, InboxMessageRegistry registry, IInboxSerializer serializer, TcjInboxOptions options)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _storages = storages?.ToArray() ?? throw new ArgumentNullException(nameof(storages));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_validated) return Task.CompletedTask;
        _options.Validate();
        if (_storages.Count != 1 || !ReferenceEquals(_storages[0], _storage)) throw new InvalidOperationException("Transactional Inbox requires exactly one provider-specific storage registration.");
        _ = _registry.RegisteredNames;
        _ = _serializer;
        string? provider = _dbContext.Database.ProviderName;
        if (string.IsNullOrWhiteSpace(provider)) throw new InvalidOperationException("Transactional Inbox requires a configured EF Core provider.");
        if (!string.Equals(provider, _storage.ProviderName, StringComparison.Ordinal)) throw new InvalidOperationException($"Inbox storage provider '{_storage.ProviderName}' does not match DbContext provider '{provider}'.");
        IEntityType? entity = _dbContext.Model.FindEntityType(typeof(InboxMessage));
        if (entity is null || !string.Equals(entity.GetTableName(), "TCJ_InboxMessages", StringComparison.Ordinal)) throw new InvalidOperationException("The TCJ Inbox entity is not mapped. Call modelBuilder.AddTcjInbox() and apply a consumer-controlled migration.");
        string[] required = [nameof(InboxMessage.Id), nameof(InboxMessage.MessageId), nameof(InboxMessage.ConsumerName), nameof(InboxMessage.MessageType), nameof(InboxMessage.MessageVersion), nameof(InboxMessage.ReceivedAtUtc), nameof(InboxMessage.AttemptCount), nameof(InboxMessage.Status), nameof(InboxMessage.ProcessedAtUtc), nameof(InboxMessage.LastErrorType), nameof(InboxMessage.CreatedAtUtc), nameof(InboxMessage.PayloadHash)];
        foreach (string property in required) if (entity.FindProperty(property) is null) throw new InvalidOperationException($"TCJ Inbox mapping is missing required property '{property}'.");
        bool unique = entity.GetIndexes().Any(index => index.IsUnique && index.Properties.Select(static p => p.Name).SequenceEqual([nameof(InboxMessage.ConsumerName), nameof(InboxMessage.MessageId)]));
        if (!unique) throw new InvalidOperationException("TCJ Inbox mapping must enforce UNIQUE (ConsumerName, MessageId).");
        _validated = true;
        return Task.CompletedTask;
    }
}
