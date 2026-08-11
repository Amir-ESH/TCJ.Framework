using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using TCJ.Core.DomainEvents;

namespace TCJ.EntityFrameworkCore.Outbox.Interceptors;

internal sealed class OutboxCaptureTracker
{
    private readonly ConditionalWeakTable<DbContext, OutboxCaptureState> _states = new();

    internal OutboxCaptureState GetOrCreate(DbContext context) => _states.GetOrCreateValue(context);

    internal bool TryGet(DbContext context, out OutboxCaptureState state) => _states.TryGetValue(context, out state!);

    internal void Remove(DbContext context) => _states.Remove(context);
}

internal sealed class OutboxCaptureState
{
    internal Dictionary<IDomainEvent, CapturedOutboxEvent> Captured { get; } = new(ReferenceEqualityComparer.Instance);
    internal HashSet<IHasDomainEvents> Aggregates { get; } = new(ReferenceEqualityComparer.Instance);
    internal Dictionary<object, EntityState> OriginalStates { get; } = new(ReferenceEqualityComparer.Instance);
    internal bool AwaitingExplicitCommit { get; set; }
    internal bool HadSaveFailure { get; set; }
}

internal sealed class CapturedOutboxEvent(IDomainEvent domainEvent, OutboxMessage message)
{
    internal IDomainEvent DomainEvent { get; } = domainEvent;
    internal OutboxMessage Message { get; } = message;
    internal bool Persisted { get; set; }
    internal bool PersistTelemetryRecorded { get; set; }
}
