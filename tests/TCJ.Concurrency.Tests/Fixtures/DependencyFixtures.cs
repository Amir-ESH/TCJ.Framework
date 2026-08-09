using System.Collections.Concurrent;
using TCJ.Core.DomainEvents;
using TCJ.DependencyInjection.Lifetimes;

namespace TCJ.Concurrency.Tests.Fixtures;

public interface ITransientProbe : ITransientDependency
{
    Guid Id { get; }
}
public sealed class TransientProbe : ITransientProbe
{
    public Guid Id { get; } = Guid.NewGuid();
}

public interface IScopedProbe : IScopedDependency
{
    Guid Id { get; }
}
public sealed class ScopedProbe : IScopedProbe
{
    public Guid Id { get; } = Guid.NewGuid();
}

public interface ISingletonProbe : ISingletonDependency
{
    Guid Id { get; }
}
public sealed class SingletonProbe : ISingletonProbe
{
    public Guid Id { get; } = Guid.NewGuid();
}

public interface IDisposalProbe : IScopedDependency
{
    Guid Id { get; }
    bool IsDisposed { get; }
}
public sealed class DisposalProbe : IDisposalProbe, IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public bool IsDisposed { get; private set; }
    public void Dispose() => IsDisposed = true;
}

public interface IOperationRecorder : IScopedDependency
{
    void Record(string operationId);
    IReadOnlyCollection<string> Records { get; }
}
public sealed class OperationRecorder : IOperationRecorder
{
    private readonly ConcurrentQueue<string> _records = new();
    public IReadOnlyCollection<string> Records => _records.ToArray();
    public void Record(string operationId) => _records.Enqueue(operationId);
}

public sealed record StressDomainEvent(string OperationId, DateTimeOffset OccurredOn) : IDomainEvent;

public sealed class StressDomainEventHandler(IOperationRecorder recorder) : IDomainEventHandler<StressDomainEvent>
{
    public Task HandleAsync(StressDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        recorder.Record(domainEvent.OperationId);
        return Task.CompletedTask;
    }
}
