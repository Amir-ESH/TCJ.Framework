using System.ComponentModel.DataAnnotations.Schema;
using TCJ.Core.DomainEvents;

namespace TCJ.Messaging.Sagas.EntityFrameworkCore.Persistence;

internal sealed class SagaInstance : IHasDomainEvents
{
    private readonly List<IDomainEvent> _domainEvents = [];

    private SagaInstance() { }

    internal SagaInstance(
        Guid sagaId,
        string sagaType,
        int definitionVersion,
        int stateSchemaVersion,
        string currentState,
        string statePayload,
        DateTimeOffset now)
    {
        SagaId = sagaId;
        SagaType = sagaType;
        DefinitionVersion = definitionVersion;
        StateSchemaVersion = stateSchemaVersion;
        CurrentState = currentState;
        Status = SagaStatus.Active;
        StatePayload = statePayload;
        CreatedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public Guid SagaId { get; private set; }
    public string SagaType { get; private set; } = string.Empty;
    public int DefinitionVersion { get; internal set; }
    public int StateSchemaVersion { get; internal set; }
    public string CurrentState { get; internal set; } = string.Empty;
    public SagaStatus Status { get; internal set; }
    public string StatePayload { get; internal set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; internal set; }
    public DateTimeOffset? CompletedAtUtc { get; internal set; }
    public DateTimeOffset? FailedAtUtc { get; internal set; }
    public SagaCompensationStatus CompensationStatus { get; internal set; }
    public int CompensationAttemptCount { get; internal set; }
    public DateTimeOffset? NextCompensationAttemptAtUtc { get; internal set; }
    public string? LastCompensationFailureType { get; internal set; }
    public byte[] ConcurrencyToken { get; private set; } = [];

    [NotMapped]
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents;

    internal void AddDomainEvent(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
    public void ClearDomainEvents() => _domainEvents.Clear();
}
