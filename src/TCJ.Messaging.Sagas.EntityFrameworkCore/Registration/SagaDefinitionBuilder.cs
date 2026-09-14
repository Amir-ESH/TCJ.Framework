using Microsoft.Extensions.DependencyInjection;
using TCJ.Messaging.Sagas.Migration;

namespace TCJ.Messaging.Sagas.EntityFrameworkCore.Registration;

/// <summary>Explicitly defines stable message, state, timeout, and compensation contracts for one Saga type.</summary>
/// <typeparam name="TSaga">Saga handler implementation.</typeparam>
/// <typeparam name="TState">Strongly typed Saga state.</typeparam>
public sealed class SagaDefinitionBuilder<TSaga, TState>
    where TSaga : class, ISaga<TState>
    where TState : class, ISagaState
{
    private readonly IServiceCollection _services;
    private readonly ISagaMessageRegistrar _messageRegistrar;
    private readonly SagaDefinitionRegistration _definition;

    internal SagaDefinitionBuilder(IServiceCollection services, ISagaMessageRegistrar messageRegistrar, SagaDefinitionRegistration definition)
    {
        _services = services;
        _messageRegistrar = messageRegistrar;
        _definition = definition;
    }

    /// <summary>Registers stable terminal state names used when lifecycle methods complete, fail, or compensate.</summary>
    /// <param name="completed">State name persisted for successful completion.</param>
    /// <param name="failed">State name persisted for terminal failure.</param>
    /// <param name="compensating">State name persisted while explicit compensation is pending.</param>
    /// <param name="compensated">State name persisted after compensation completes.</param>
    /// <returns>This definition builder.</returns>
    public SagaDefinitionBuilder<TSaga, TState> TerminalStates(string completed, string failed, string compensating, string compensated)
    {
        ValidateState(completed, nameof(completed));
        ValidateState(failed, nameof(failed));
        ValidateState(compensating, nameof(compensating));
        ValidateState(compensated, nameof(compensated));
        string[] values = [completed, failed, compensating, compensated];
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new InvalidOperationException("Saga terminal state names must be distinct.");
        _definition.CompletedState = completed;
        _definition.FailedState = failed;
        _definition.CompensatingState = compensating;
        _definition.CompensatedState = compensated;
        return this;
    }

    /// <summary>Registers a message as a legal Saga starter with deterministic primary correlation.</summary>
    /// <typeparam name="TMessage">Start message CLR contract.</typeparam>
    /// <param name="messageType">Stable logical message type.</param>
    /// <param name="messageVersion">Positive message schema version.</param>
    /// <param name="initialState">Stable initial Saga state name.</param>
    /// <param name="correlationName">Stable logical correlation name.</param>
    /// <param name="correlate">Deterministic side-effect-free correlation selector.</param>
    /// <returns>This definition builder.</returns>
    public SagaDefinitionBuilder<TSaga, TState> StartsWith<TMessage>(
        string messageType,
        int messageVersion,
        string initialState,
        string correlationName,
        Func<TMessage, SagaCorrelationKey> correlate)
    {
        ArgumentNullException.ThrowIfNull(correlate);
        EnsureHandlerInterface(typeof(ISagaStartsWith<TState, TMessage>), "start");
        ValidateState(initialState, nameof(initialState));
        ValidateCorrelationName(correlationName);
        SagaMessageRegistration message = GetOrAddMessage<TMessage>(messageType, messageVersion);
        if (message.Start is not null) throw new InvalidOperationException($"Saga message '{typeof(TMessage).FullName}' is already registered as a starter.");
        if (message.Continuation is { } continuation && !string.Equals(continuation.CorrelationName, correlationName, StringComparison.Ordinal))
            throw new InvalidOperationException($"Saga message '{typeof(TMessage).FullName}' has conflicting start/continuation correlation names.");
        message.Start = new SagaStartRegistration(
            initialState,
            correlationName,
            value => correlate((TMessage)value),
            static async (services, state, incoming, context, token) =>
                await ((ISagaStartsWith<TState, TMessage>)services.GetRequiredService<TSaga>()).HandleAsync((TState)state, (TMessage)incoming, context, token).ConfigureAwait(false));
        return this;
    }

    /// <summary>Registers a correlated continuation with explicit allowed-state and bounded missing/invalid policies.</summary>
    /// <typeparam name="TMessage">Continuation message CLR contract.</typeparam>
    /// <param name="messageType">Stable logical message type.</param>
    /// <param name="messageVersion">Positive message schema version.</param>
    /// <param name="correlationName">Stable logical correlation name.</param>
    /// <param name="correlate">Deterministic side-effect-free correlation selector.</param>
    /// <param name="allowedStates">Explicit states in which the continuation is legal.</param>
    /// <param name="missingPolicy">Bounded policy for a missing Saga instance.</param>
    /// <param name="invalidTransitionPolicy">Bounded policy for an invalid or out-of-order transition.</param>
    /// <param name="allowTerminal">Whether this specific continuation may run for a terminal Saga.</param>
    /// <returns>This definition builder.</returns>
    public SagaDefinitionBuilder<TSaga, TState> Handles<TMessage>(
        string messageType,
        int messageVersion,
        string correlationName,
        Func<TMessage, SagaCorrelationKey> correlate,
        IEnumerable<string> allowedStates,
        SagaMissingInstancePolicy missingPolicy = SagaMissingInstancePolicy.Retry,
        SagaInvalidTransitionPolicy invalidTransitionPolicy = SagaInvalidTransitionPolicy.Retry,
        bool allowTerminal = false)
    {
        ArgumentNullException.ThrowIfNull(correlate);
        EnsureHandlerInterface(typeof(ISagaHandles<TState, TMessage>), "continuation");
        ArgumentNullException.ThrowIfNull(allowedStates);
        ValidateCorrelationName(correlationName);
        HashSet<string> states = ValidateStates(allowedStates);
        SagaMessageRegistration message = GetOrAddMessage<TMessage>(messageType, messageVersion);
        if (message.Continuation is not null) throw new InvalidOperationException($"Saga message '{typeof(TMessage).FullName}' already has a continuation registration.");
        if (message.Start is { } start && !string.Equals(start.CorrelationName, correlationName, StringComparison.Ordinal))
            throw new InvalidOperationException($"Saga message '{typeof(TMessage).FullName}' has conflicting start/continuation correlation names.");
        message.Continuation = new SagaContinuationRegistration(
            correlationName,
            value => correlate((TMessage)value),
            states,
            missingPolicy,
            invalidTransitionPolicy,
            allowTerminal,
            static async (services, state, incoming, context, token) =>
                await ((ISagaHandles<TState, TMessage>)services.GetRequiredService<TSaga>()).HandleAsync((TState)state, (TMessage)incoming, context, token).ConfigureAwait(false));
        return this;
    }

    /// <summary>Registers one durable timeout contract. Timer names are Saga-owned, bounded, and not a general scheduler API.</summary>
    /// <param name="timerName">Stable registered Saga timer name.</param>
    /// <param name="timeoutType">Stable logical timeout contract name.</param>
    /// <param name="allowedStates">Explicit states in which the timeout transition is legal.</param>
    /// <param name="invalidTransitionPolicy">Bounded policy for an invalid timeout transition.</param>
    /// <returns>This definition builder.</returns>
    public SagaDefinitionBuilder<TSaga, TState> HandlesTimeout(
        string timerName,
        string timeoutType,
        IEnumerable<string> allowedStates,
        SagaInvalidTransitionPolicy invalidTransitionPolicy = SagaInvalidTransitionPolicy.Retry)
    {
        EnsureHandlerInterface(typeof(ISagaHandlesTimeout<TState>), "timeout");
        ValidateTimerName(timerName);
        ValidateLogicalName(timeoutType, nameof(timeoutType), 128);
        ArgumentNullException.ThrowIfNull(allowedStates);
        if (_definition.Timers.ContainsKey(timerName)) throw new InvalidOperationException($"Saga timer '{timerName}' is already registered.");
        HashSet<string> states = ValidateStates(allowedStates);
        _definition.Timers.Add(timerName, new SagaTimeoutRegistration(
            timerName,
            timeoutType,
            states,
            invalidTransitionPolicy,
            static async (services, state, timeout, context, token) =>
                await ((ISagaHandlesTimeout<TState>)services.GetRequiredService<TSaga>()).HandleTimeoutAsync((TState)state, timeout, context, token).ConfigureAwait(false)));
        return this;
    }

    /// <summary>Registers explicit application-defined compensation behavior.</summary>
    /// <returns>This definition builder.</returns>
    public SagaDefinitionBuilder<TSaga, TState> Compensates()
    {
        EnsureHandlerInterface(typeof(ISagaCompensates<TState>), "compensation");
        if (_definition.CompensationHandler is not null) throw new InvalidOperationException("Saga compensation behavior is already registered.");
        _definition.CompensationHandler = static async (services, state, context, token) =>
            await ((ISagaCompensates<TState>)services.GetRequiredService<TSaga>()).CompensateAsync((TState)state, context, token).ConfigureAwait(false);
        return this;
    }

    /// <summary>Declares an older active definition version that current code is allowed to load and migrate transactionally.</summary>
    /// <param name="version">Positive historical definition version lower than the current definition version.</param>
    /// <returns>This definition builder.</returns>
    public SagaDefinitionBuilder<TSaga, TState> SupportsDefinitionVersion(int version)
    {
        if (version <= 0 || version >= _definition.DefinitionVersion)
            throw new ArgumentOutOfRangeException(nameof(version), "Supported historical definition versions must be positive and older than the current definition.");
        _definition.SupportedDefinitionVersions.Add(version);
        return this;
    }

    /// <summary>Registers one explicit state-payload migration edge.</summary>
    /// <typeparam name="TMigrator">Registered migrator implementation.</typeparam>
    /// <param name="fromVersion">Source state-schema version.</param>
    /// <param name="toVersion">Target state-schema version.</param>
    /// <returns>This definition builder.</returns>
    public SagaDefinitionBuilder<TSaga, TState> AddStateMigrator<TMigrator>(int fromVersion, int toVersion)
        where TMigrator : class, ISagaStateMigrator
    {
        if (fromVersion <= 0 || toVersion <= fromVersion || toVersion > _definition.StateSchemaVersion)
            throw new ArgumentOutOfRangeException(nameof(fromVersion), "Saga state migration versions must move forward toward the current state schema version.");
        if (_definition.Migrators.ContainsKey(fromVersion)) throw new InvalidOperationException($"A Saga state migrator from v{fromVersion} is already registered.");
        _services.AddScoped<TMigrator>();
        _definition.Migrators.Add(fromVersion, new SagaMigratorRegistration(fromVersion, toVersion, typeof(TMigrator)));
        return this;
    }

    private SagaMessageRegistration GetOrAddMessage<TMessage>(string messageType, int messageVersion)
    {
        ValidateLogicalName(messageType, nameof(messageType), 128);
        if (messageVersion <= 0) throw new ArgumentOutOfRangeException(nameof(messageVersion));
        if (_definition.Messages.TryGetValue(typeof(TMessage), out SagaMessageRegistration? existing))
        {
            if (!string.Equals(existing.MessageName, messageType, StringComparison.Ordinal) || existing.MessageVersion != messageVersion)
                throw new InvalidOperationException($"Saga message CLR type '{typeof(TMessage).FullName}' was already registered with a different stable contract.");
            return existing;
        }
        _messageRegistrar.Register<TMessage>(_definition.SagaType, messageType, messageVersion);
        var registration = new SagaMessageRegistration { MessageType = typeof(TMessage), MessageName = messageType, MessageVersion = messageVersion };
        _definition.Messages.Add(typeof(TMessage), registration);
        return registration;
    }

    private static HashSet<string> ValidateStates(IEnumerable<string> states)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string state in states)
        {
            ValidateState(state, nameof(states));
            if (!result.Add(state)) throw new InvalidOperationException($"Saga allowed state '{state}' is duplicated.");
        }
        if (result.Count == 0) throw new InvalidOperationException("At least one allowed Saga state is required.");
        return result;
    }

    private static void EnsureHandlerInterface(Type requiredInterface, string role)
    {
        if (!requiredInterface.IsAssignableFrom(typeof(TSaga)))
            throw new InvalidOperationException($"Saga implementation '{typeof(TSaga).FullName}' must implement '{requiredInterface.FullName}' before it can register a {role} handler.");
    }

    private static void ValidateState(string value, string parameter) => ValidateLogicalName(value, parameter, 64);
    private static void ValidateCorrelationName(string value) => ValidateLogicalName(value, nameof(value), 64);
    private static void ValidateTimerName(string value) => ValidateLogicalName(value, nameof(value), 128);
    private static void ValidateLogicalName(string value, string parameter, int maximum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        if (value.Length > maximum) throw new ArgumentOutOfRangeException(parameter, $"Value cannot exceed {maximum} characters.");
        if (value.Any(char.IsControl)) throw new ArgumentException("Stable Saga contract names cannot contain control characters.", parameter);
    }
}

internal interface ISagaMessageRegistrar
{
    void Register<TMessage>(string sagaType, string messageType, int messageVersion);
}
