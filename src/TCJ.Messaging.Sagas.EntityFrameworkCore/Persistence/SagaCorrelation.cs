namespace TCJ.Messaging.Sagas.EntityFrameworkCore.Persistence;

internal sealed class SagaCorrelation
{
    private SagaCorrelation() { }

    internal SagaCorrelation(Guid sagaId, string sagaType, string correlationName, string valueHash, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7(now);
        SagaId = sagaId;
        SagaType = sagaType;
        CorrelationName = correlationName;
        ValueHash = valueHash;
        CreatedAtUtc = now;
    }

    public Guid Id { get; private set; }
    public Guid SagaId { get; private set; }
    public string SagaType { get; private set; } = string.Empty;
    public string CorrelationName { get; private set; } = string.Empty;
    public string ValueHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? RemovedAtUtc { get; internal set; }
}
