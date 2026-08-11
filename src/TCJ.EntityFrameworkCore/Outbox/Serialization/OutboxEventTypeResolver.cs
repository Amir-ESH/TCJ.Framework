using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using TCJ.Core.DomainEvents;

namespace TCJ.EntityFrameworkCore.Outbox.Serialization;

/// <summary>
/// Resolves explicit stable event names and a versioned CLR-name convention without using assembly-qualified names.
/// </summary>
internal sealed class OutboxEventTypeResolver : IOutboxEventTypeResolver
{
    private const int MaximumEventNameLength = 128;
    private readonly IReadOnlyDictionary<Type, string> _namesByType;
    private readonly IReadOnlyDictionary<string, Type> _typesByName;
    private readonly ConcurrentDictionary<Type, string> _conventionNames = new();
    private readonly ConcurrentDictionary<string, Type> _conventionTypes = new(StringComparer.Ordinal);

    /// <summary>Creates the resolver from explicitly registered event contracts.</summary>
    public OutboxEventTypeResolver(IEnumerable<OutboxEventRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var byType = new Dictionary<Type, string>();
        var byName = new Dictionary<string, Type>(StringComparer.Ordinal);

        foreach (OutboxEventRegistration registration in registrations)
        {
            ValidateEventType(registration.EventType);
            ValidateName(registration.EventName);

            if (!byType.TryAdd(registration.EventType, registration.EventName))
            {
                throw new InvalidOperationException($"Outbox event type '{registration.EventType.FullName}' is registered more than once.");
            }

            if (!byName.TryAdd(registration.EventName, registration.EventType))
            {
                throw new InvalidOperationException($"Outbox event name '{registration.EventName}' is registered for more than one CLR type.");
            }
        }

        _namesByType = byType;
        _typesByName = byName;
    }

    /// <inheritdoc />
    public string GetName(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        ValidateEventType(eventType);
        return _namesByType.TryGetValue(eventType, out string? name)
            ? name
            : _conventionNames.GetOrAdd(eventType, CreateConventionName);
    }

    /// <inheritdoc />
    public Type Resolve(string eventTypeName)
    {
        ValidateName(eventTypeName);
        if (_typesByName.TryGetValue(eventTypeName, out Type? registered))
        {
            return registered;
        }

        if (_conventionTypes.TryGetValue(eventTypeName, out Type? cached))
        {
            return cached;
        }

        Type[] matches = AppDomain.CurrentDomain.GetAssemblies()
            .OrderBy(static assembly => assembly.GetName().Name, StringComparer.Ordinal)
            .SelectMany(GetLoadableTypes)
            .Where(static type => type is { IsAbstract: false, IsInterface: false })
            .Where(static type => typeof(IDomainEvent).IsAssignableFrom(type))
            .Where(type => string.Equals(CreateConventionName(type), eventTypeName, StringComparison.Ordinal))
            .Distinct()
            .Take(2)
            .ToArray();

        if (matches.Length == 0)
        {
            throw new InvalidOperationException($"Outbox event type name '{eventTypeName}' is unknown. Register the event explicitly before processing persisted messages.");
        }

        if (matches.Length > 1)
        {
            throw new InvalidOperationException($"Outbox event type name '{eventTypeName}' is ambiguous. Register an explicit unique logical event name.");
        }

        _conventionTypes[eventTypeName] = matches[0];
        _conventionNames[matches[0]] = eventTypeName;
        return matches[0];
    }

    private static string CreateConventionName(Type eventType)
    {
        string source = eventType.FullName ?? eventType.Name;
        var builder = new StringBuilder("clr.");
        bool previousSeparator = false;
        foreach (char character in source)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                previousSeparator = false;
            }
            else if (!previousSeparator)
            {
                builder.Append('.');
                previousSeparator = true;
            }
        }

        string name = builder.ToString().TrimEnd('.') + ".v1";
        ValidateName(name);
        return name;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }

    private static void ValidateEventType(Type eventType)
    {
        if (!typeof(IDomainEvent).IsAssignableFrom(eventType))
        {
            throw new ArgumentException($"Type '{eventType.FullName}' must implement IDomainEvent.", nameof(eventType));
        }
    }

    internal static void ValidateName(string eventName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        if (eventName.Length > MaximumEventNameLength)
        {
            throw new ArgumentException($"Outbox event names must be {MaximumEventNameLength} characters or fewer.", nameof(eventName));
        }

        if (eventName.Any(static character => !(char.IsLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            throw new ArgumentException("Outbox event names may contain only letters, numbers, '.', '-' and '_'.", nameof(eventName));
        }
    }
}
