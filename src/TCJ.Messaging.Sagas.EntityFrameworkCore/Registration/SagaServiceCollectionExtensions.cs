using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TCJ.Core.Identifiers;
using TCJ.Core.Resilience;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.Messaging.Sagas.Configuration;
using TCJ.Messaging.Sagas.EntityFrameworkCore.HealthChecks;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Processing;
using TCJ.Messaging.Sagas.Processing;
using TCJ.Messaging.Sagas.Remediation;

namespace TCJ.Messaging.Sagas.EntityFrameworkCore.Registration;

/// <summary>Registers provider-neutral durable Saga persistence and orchestration.</summary>
public static class SagaServiceCollectionExtensions
{
    /// <summary>
    /// Enables durable Saga infrastructure for the same consumer-owned DbContext used by the transactional Inbox and Outbox.
    /// A provider-specific Saga storage implementation must also be registered.
    /// </summary>
    /// <typeparam name="TDbContext">Application DbContext that owns Inbox, Outbox, business, and Saga state.</typeparam>
    /// <param name="services">Service collection.</param>
    /// <param name="configure">Optional bounded Saga configuration.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTcjSagas<TDbContext>(this IServiceCollection services, Action<TcjSagaOptions>? configure = null)
        where TDbContext : DbContext, IReadDbContext, IWriteDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        EnsureSingleContext<TDbContext>(services);

        var options = new TcjSagaOptions();
        configure?.Invoke(options);
        options.Validate();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IGuidGenerator, GuidGenerator>();
        services.TryAddSingleton(options);
        services.TryAddSingleton<ITransientFailureDetector, TransientFailureDetector>();
        services.TryAddSingleton<SagaDefinitionRegistry>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITransientFailureClassifier, SagaRetryFailureClassifier>());
        services.TryAddScoped<ISagaStartupValidator, SagaStartupValidator<TDbContext>>();
        services.TryAddSingleton<SagaTimerProcessor<TDbContext>>();
        services.TryAddSingleton<ISagaTimerProcessor>(static provider => provider.GetRequiredService<SagaTimerProcessor<TDbContext>>());
        services.TryAddSingleton<SagaRemediationService<TDbContext>>();
        services.TryAddSingleton<ISagaRemediationService>(static provider => provider.GetRequiredService<SagaRemediationService<TDbContext>>());
        services.AddHealthChecks().AddTcjSagas();
        return services;
    }

    /// <summary>Registers one Saga definition, its AOT-safe state metadata, and its explicit message/timeout contracts.</summary>
    /// <typeparam name="TDbContext">Application DbContext shared with transactional Inbox and Outbox.</typeparam>
    /// <typeparam name="TSaga">Saga handler implementation.</typeparam>
    /// <typeparam name="TState">Strongly typed Saga state.</typeparam>
    /// <param name="services">Service collection.</param>
    /// <param name="sagaType">Stable logical Saga contract name.</param>
    /// <param name="definitionVersion">Positive definition version.</param>
    /// <param name="stateSchemaVersion">Positive serialized-state schema version.</param>
    /// <param name="stateJsonTypeInfo">Explicit System.Text.Json metadata for the state type.</param>
    /// <param name="stateFactory">AOT-safe state factory that does not require runtime activation.</param>
    /// <param name="configure">Explicit Saga definition configuration.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTcjSaga<TDbContext, TSaga, TState>(
        this IServiceCollection services,
        string sagaType,
        int definitionVersion,
        int stateSchemaVersion,
        JsonTypeInfo<TState> stateJsonTypeInfo,
        Func<TState> stateFactory,
        Action<SagaDefinitionBuilder<TSaga, TState>> configure)
        where TDbContext : DbContext, IReadDbContext, IWriteDbContext
        where TSaga : class, ISaga<TState>
        where TState : class, ISagaState
    {
        ArgumentNullException.ThrowIfNull(services);
        ValidateLogicalName(sagaType, nameof(sagaType), 128);
        if (definitionVersion <= 0) throw new ArgumentOutOfRangeException(nameof(definitionVersion));
        if (stateSchemaVersion <= 0) throw new ArgumentOutOfRangeException(nameof(stateSchemaVersion));
        ArgumentNullException.ThrowIfNull(stateJsonTypeInfo);
        ArgumentNullException.ThrowIfNull(stateFactory);
        ArgumentNullException.ThrowIfNull(configure);
        if (stateJsonTypeInfo.Type != typeof(TState))
            throw new InvalidOperationException($"Saga state JsonTypeInfo must describe '{typeof(TState).FullName}'.");

        lock (services)
        {
            if (!services.Any(static descriptor => descriptor.ServiceType == typeof(SagaContextRegistration)))
                throw new InvalidOperationException("Call AddTcjSagas<TDbContext>() before registering Saga definitions.");
            SagaDefinitionRegistration? duplicate = services
                .Where(static descriptor => descriptor.ServiceType == typeof(SagaDefinitionRegistration))
                .Select(static descriptor => descriptor.ImplementationInstance)
                .OfType<SagaDefinitionRegistration>()
                .FirstOrDefault(definition => string.Equals(definition.SagaType, sagaType, StringComparison.Ordinal));
            if (duplicate is not null)
                throw new InvalidOperationException($"Saga type '{sagaType}' is already registered. SagaType values must be globally unique in one service container.");
        }

        var definition = new SagaDefinitionRegistration
        {
            SagaType = sagaType,
            DefinitionVersion = definitionVersion,
            StateSchemaVersion = stateSchemaVersion,
            StateType = typeof(TState),
            StateJsonTypeInfo = stateJsonTypeInfo,
            StateFactory = () => stateFactory() ?? throw new InvalidOperationException("Saga state factory returned null."),
            SagaImplementationType = typeof(TSaga)
        };
        var registrar = new SagaMessageRegistrar<TDbContext>(services);
        var builder = new SagaDefinitionBuilder<TSaga, TState>(services, registrar, definition);
        configure(builder);
        SagaStateRuntime.ValidateDefinition(definition);

        services.TryAddScoped<TSaga>();
        services.AddSingleton(definition);
        return services;
    }

    private static void EnsureSingleContext<TDbContext>(IServiceCollection services) where TDbContext : DbContext
    {
        lock (services)
        {
            ServiceDescriptor? descriptor = services.FirstOrDefault(static item => item.ServiceType == typeof(SagaContextRegistration));
            if (descriptor?.ImplementationInstance is SagaContextRegistration existing)
            {
                if (existing.DbContextType != typeof(TDbContext))
                    throw new InvalidOperationException($"TCJ Sagas are already registered for DbContext '{existing.DbContextType.Name}'. Register one Saga DbContext per service container.");
                return;
            }
            services.AddSingleton(new SagaContextRegistration(typeof(TDbContext)));
        }
    }

    private static void ValidateLogicalName(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength) throw new ArgumentOutOfRangeException(parameterName, $"Value cannot exceed {maximumLength} characters.");
        if (value.Any(char.IsControl)) throw new ArgumentException("Stable Saga contract names cannot contain control characters.", parameterName);
    }
}
