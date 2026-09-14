using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Inbox.Extensions;
using TCJ.EntityFrameworkCore.Outbox.Extensions;
using TCJ.Messaging.Sagas.Configuration;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Persistence;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Registration;

namespace TCJ.Messaging.Sagas.EntityFrameworkCore.Processing;

internal sealed class SagaStartupValidator<TDbContext> : ISagaStartupValidator
    where TDbContext : DbContext, IReadDbContext, IWriteDbContext
{
    private readonly TDbContext _dbContext;
    private readonly TcjSagaOptions _options;
    private readonly SagaDefinitionRegistry _definitions;
    private readonly IReadOnlyList<ISagaProviderStorage> _providerStorages;
    private readonly IReadOnlyList<InboxContextRegistration> _inboxContexts;
    private readonly IReadOnlyList<OutboxContextRegistration> _outboxContexts;
    private readonly SagaContextRegistration _sagaContext;
    private bool _validated;

    public SagaStartupValidator(
        TDbContext dbContext,
        TcjSagaOptions options,
        SagaDefinitionRegistry definitions,
        IEnumerable<ISagaProviderStorage> providerStorages,
        IEnumerable<InboxContextRegistration> inboxContexts,
        IEnumerable<OutboxContextRegistration> outboxContexts,
        SagaContextRegistration sagaContext)
    {
        _dbContext = dbContext;
        _options = options;
        _definitions = definitions;
        _providerStorages = providerStorages.ToArray();
        _inboxContexts = inboxContexts.ToArray();
        _outboxContexts = outboxContexts.ToArray();
        _sagaContext = sagaContext;
    }

    public Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_validated) return Task.CompletedTask;
        _options.Validate();
        if (_sagaContext.DbContextType != typeof(TDbContext)) throw new InvalidOperationException("Saga DbContext registration is inconsistent.");
        if (_inboxContexts.Count != 1 || _inboxContexts[0].DbContextType != typeof(TDbContext))
            throw new InvalidOperationException("Saga processing requires the existing transactional Inbox to use the same TDbContext.");
        if (_outboxContexts.Count != 1 || _outboxContexts[0].DbContextType != typeof(TDbContext))
            throw new InvalidOperationException("Saga durable output requires the existing transactional Outbox to use the same TDbContext.");
        if (_providerStorages.Count != 1) throw new InvalidOperationException("Saga persistence requires exactly one provider-specific storage registration.");

        string providerName = _dbContext.Database.ProviderName ?? throw new InvalidOperationException("Saga persistence requires a configured EF Core provider.");
        if (!string.Equals(providerName, _providerStorages[0].ProviderName, StringComparison.Ordinal))
            throw new InvalidOperationException($"Saga storage provider '{_providerStorages[0].ProviderName}' does not match DbContext provider '{providerName}'.");
        if (_definitions.Definitions.Count == 0) throw new InvalidOperationException("At least one explicit Saga definition must be registered.");
        foreach (SagaDefinitionRegistration definition in _definitions.Definitions) SagaStateRuntime.ValidateDefinition(definition);

        ValidateEntity<SagaInstance>("TCJ_SagaInstances", nameof(SagaInstance.SagaId), nameof(SagaInstance.SagaType), nameof(SagaInstance.DefinitionVersion), nameof(SagaInstance.StateSchemaVersion), nameof(SagaInstance.StatePayload), nameof(SagaInstance.ConcurrencyToken));
        ValidateEntity<SagaCorrelation>("TCJ_SagaCorrelations", nameof(SagaCorrelation.Id), nameof(SagaCorrelation.SagaId), nameof(SagaCorrelation.SagaType), nameof(SagaCorrelation.CorrelationName), nameof(SagaCorrelation.ValueHash), nameof(SagaCorrelation.RemovedAtUtc));
        ValidateEntity<SagaTimer>("TCJ_SagaTimers", nameof(SagaTimer.TimerId), nameof(SagaTimer.SagaId), nameof(SagaTimer.DueAtUtc), nameof(SagaTimer.Status), nameof(SagaTimer.ConcurrencyToken));

        IProperty instanceToken = _dbContext.Model.FindEntityType(typeof(SagaInstance))!.FindProperty(nameof(SagaInstance.ConcurrencyToken))!;
        IProperty timerToken = _dbContext.Model.FindEntityType(typeof(SagaTimer))!.FindProperty(nameof(SagaTimer.ConcurrencyToken))!;
        if (!instanceToken.IsConcurrencyToken || !timerToken.IsConcurrencyToken)
            throw new InvalidOperationException("Saga instance and timer concurrency tokens must be configured by the selected provider.");
        _validated = true;
        return Task.CompletedTask;
    }

    private void ValidateEntity<TEntity>(string expectedTable, params string[] requiredProperties)
    {
        IEntityType? entity = _dbContext.Model.FindEntityType(typeof(TEntity));
        if (entity is null || !string.Equals(entity.GetTableName(), expectedTable, StringComparison.Ordinal))
            throw new InvalidOperationException($"Saga persistence entity '{typeof(TEntity).Name}' is not mapped. Apply the provider-specific TCJ Saga model configuration and a consumer-controlled migration.");
        foreach (string property in requiredProperties)
            if (entity.FindProperty(property) is null) throw new InvalidOperationException($"Saga persistence mapping '{expectedTable}' is missing required property '{property}'.");
    }
}
