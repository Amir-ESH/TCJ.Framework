using System.Text.Json.Serialization.Metadata;
using TCJ.Messaging.Sagas.Migration;

namespace TCJ.Messaging.Sagas.EntityFrameworkCore.Registration;

internal delegate Task SagaMessageHandlerInvoker(IServiceProvider services, object state, object message, SagaContext context, CancellationToken cancellationToken);
internal delegate Task SagaTimeoutHandlerInvoker(IServiceProvider services, object state, SagaTimeout timeout, SagaContext context, CancellationToken cancellationToken);
internal delegate Task SagaCompensationHandlerInvoker(IServiceProvider services, object state, SagaContext context, CancellationToken cancellationToken);

internal sealed class SagaDefinitionRegistration
{
    internal required string SagaType { get; init; }
    internal required int DefinitionVersion { get; init; }
    internal required int StateSchemaVersion { get; init; }
    internal required Type StateType { get; init; }
    internal required JsonTypeInfo StateJsonTypeInfo { get; init; }
    internal required Func<object> StateFactory { get; init; }
    internal required Type SagaImplementationType { get; init; }
    internal Dictionary<Type, SagaMessageRegistration> Messages { get; } = [];
    internal Dictionary<string, SagaTimeoutRegistration> Timers { get; } = new(StringComparer.Ordinal);
    internal Dictionary<int, SagaMigratorRegistration> Migrators { get; } = [];
    internal HashSet<int> SupportedDefinitionVersions { get; } = [];
    internal string? CompletedState { get; set; }
    internal string? FailedState { get; set; }
    internal string? CompensatingState { get; set; }
    internal string? CompensatedState { get; set; }
    internal SagaCompensationHandlerInvoker? CompensationHandler { get; set; }
}

internal sealed class SagaMessageRegistration
{
    internal required Type MessageType { get; init; }
    internal required string MessageName { get; init; }
    internal required int MessageVersion { get; init; }
    internal SagaStartRegistration? Start { get; set; }
    internal SagaContinuationRegistration? Continuation { get; set; }
}

internal sealed record SagaStartRegistration(
    string InitialState,
    string CorrelationName,
    Func<object, SagaCorrelationKey> Correlate,
    SagaMessageHandlerInvoker Invoke);

internal sealed record SagaContinuationRegistration(
    string CorrelationName,
    Func<object, SagaCorrelationKey> Correlate,
    IReadOnlySet<string> AllowedStates,
    SagaMissingInstancePolicy MissingPolicy,
    SagaInvalidTransitionPolicy InvalidTransitionPolicy,
    bool AllowTerminal,
    SagaMessageHandlerInvoker Invoke);

internal sealed record SagaTimeoutRegistration(
    string TimerName,
    string TimeoutType,
    IReadOnlySet<string> AllowedStates,
    SagaInvalidTransitionPolicy InvalidTransitionPolicy,
    SagaTimeoutHandlerInvoker Invoke);

internal sealed record SagaMigratorRegistration(int FromVersion, int ToVersion, Type MigratorType);

internal sealed record SagaMessageBinding(Type MessageType, string SagaType);
internal sealed record SagaContextRegistration(Type DbContextType);
