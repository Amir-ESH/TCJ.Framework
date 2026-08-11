using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TCJ.Core.DomainEvents;
using TCJ.Core.Outbox;
using TCJ.EntityFrameworkCore.Abstractions;

namespace TCJ.EntityFrameworkCore.Outbox.Processing;

internal sealed class OutboxStartupValidator<TDbContext> : IOutboxStartupValidator
    where TDbContext : DbContext, IReadDbContext, IWriteDbContext
{
    private static readonly string[] RequiredProperties =
    [
        nameof(OutboxMessage.Id),
        nameof(OutboxMessage.OccurredAtUtc),
        nameof(OutboxMessage.EventType),
        nameof(OutboxMessage.Payload),
        nameof(OutboxMessage.AttemptCount),
        nameof(OutboxMessage.NextAttemptAtUtc),
        nameof(OutboxMessage.ProcessedAtUtc),
        nameof(OutboxMessage.LastErrorType),
        nameof(OutboxMessage.CreatedAtUtc),
        nameof(OutboxMessage.LockExpiresAtUtc)
    ];

    private readonly TDbContext _dbContext;
    private readonly IOutboxStorage _storage;
    private readonly IReadOnlyList<IOutboxStorage> _storages;
    private readonly IOutboxSerializer _serializer;
    private readonly IOutboxEventTypeResolver _resolver;
    private readonly IDomainEventDispatcher _dispatcher;
    private readonly TcjOutboxOptions _options;
    private bool _validated;

    public OutboxStartupValidator(
        TDbContext dbContext,
        IOutboxStorage storage,
        IEnumerable<IOutboxStorage> storages,
        IOutboxSerializer serializer,
        IOutboxEventTypeResolver resolver,
        IDomainEventDispatcher dispatcher,
        TcjOutboxOptions options)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        ArgumentNullException.ThrowIfNull(storages);
        _storages = storages.ToArray();
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_validated)
        {
            return Task.CompletedTask;
        }

        _options.Validate();
        if (_storages.Count != 1 || !ReferenceEquals(_storages[0], _storage))
        {
            throw new InvalidOperationException("Transactional outbox processing requires exactly one provider-specific IOutboxStorage registration.");
        }

        _ = _serializer;
        _ = _resolver;
        _ = _dispatcher;

        string? provider = _dbContext.Database.ProviderName;
        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new InvalidOperationException("Transactional outbox processing requires a configured EF Core database provider.");
        }

        if (!string.Equals(provider, _storage.ProviderName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The registered outbox storage provider '{_storage.ProviderName}' does not match DbContext provider '{provider}'.");
        }

        IEntityType? entity = _dbContext.Model.FindEntityType(typeof(OutboxMessage));
        if (entity is null)
        {
            throw new InvalidOperationException("The TCJ outbox entity is not mapped. Call modelBuilder.AddTcjOutbox() in OnModelCreating and add a consumer-controlled migration before enabling processing.");
        }

        string? tableName = entity.GetTableName();
        if (!string.Equals(tableName, "TCJ_OutboxMessages", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The TCJ outbox entity must map to table 'TCJ_OutboxMessages'.");
        }

        foreach (string property in RequiredProperties)
        {
            if (entity.FindProperty(property) is null)
            {
                throw new InvalidOperationException($"The TCJ outbox mapping is missing required property '{property}'.");
            }
        }

        IKey? primaryKey = entity.FindPrimaryKey();
        if (primaryKey is null || primaryKey.Properties.Count != 1 || primaryKey.Properties[0].Name != nameof(OutboxMessage.Id))
        {
            throw new InvalidOperationException("The TCJ outbox mapping must enforce message uniqueness with Id as the primary key.");
        }

        string[][] requiredIndexes =
        [
            [nameof(OutboxMessage.ProcessedAtUtc), nameof(OutboxMessage.NextAttemptAtUtc)],
            [nameof(OutboxMessage.LockExpiresAtUtc)],
            [nameof(OutboxMessage.OccurredAtUtc)],
            [nameof(OutboxMessage.EventType)]
        ];

        foreach (string[] expected in requiredIndexes)
        {
            bool present = entity.GetIndexes().Any(index => index.Properties.Select(static property => property.Name).SequenceEqual(expected));
            if (!present)
            {
                throw new InvalidOperationException($"The TCJ outbox mapping is missing required index on ({string.Join(", ", expected)}).");
            }
        }

        _validated = true;
        return Task.CompletedTask;
    }
}
