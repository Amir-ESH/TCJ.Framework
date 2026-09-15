using TCJ.Messaging.Sagas.EntityFrameworkCore.Processing;

namespace TCJ.Messaging.Sagas.EntityFrameworkCore.Registration;

internal sealed class SagaDefinitionRegistry
{
    private readonly IReadOnlyCollection<SagaDefinitionRegistration> _definitions;
    private readonly IReadOnlyDictionary<string, SagaDefinitionRegistration> _byType;
    private readonly IReadOnlyDictionary<Type, (SagaDefinitionRegistration Definition, SagaMessageRegistration Message)> _byMessage;

    public SagaDefinitionRegistry(IEnumerable<SagaDefinitionRegistration> definitions)
    {
        SagaDefinitionRegistration[] all = definitions.ToArray();
        _definitions = all;
        _byType = all.ToDictionary(x => x.SagaType, StringComparer.Ordinal);
        _byMessage = all.SelectMany(d => d.Messages.Values.Select(m => (Definition: d, Message: m)))
            .ToDictionary(x => x.Message.MessageType, x => x);
    }

    internal IReadOnlyCollection<SagaDefinitionRegistration> Definitions => _definitions;
    internal SagaDefinitionRegistration Get(string sagaType) => _byType.TryGetValue(sagaType, out var value) ? value : throw new SagaValidationException("UnknownSagaType");
    internal (SagaDefinitionRegistration Definition, SagaMessageRegistration Message) GetByMessage(Type messageType) =>
        _byMessage.TryGetValue(messageType, out var value) ? value : throw new SagaValidationException("UnknownSagaMessage");
}
