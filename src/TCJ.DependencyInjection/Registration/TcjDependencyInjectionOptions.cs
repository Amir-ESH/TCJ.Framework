using System.Reflection;

namespace TCJ.DependencyInjection.Registration;

/// <summary>
/// Configures TCJ convention-based dependency registration.
/// </summary>
public sealed class TcjDependencyInjectionOptions
{
    private readonly HashSet<Assembly> _assemblies = [];

    internal IReadOnlyCollection<Assembly> Assemblies => _assemblies;

    /// <summary>
    /// Gets or sets whether TCJ framework defaults such as the GUID generator and
    /// domain-event dispatcher are registered. The default value is <see langword="true"/>.
    /// </summary>
    public bool RegisterFrameworkServices { get; set; } = true;

    /// <summary>
    /// Gets or sets whether implementations of <c>IDomainEventHandler&lt;TEvent&gt;</c>
    /// are discovered and registered as transient services. The default value is
    /// <see langword="true"/>.
    /// </summary>
    public bool RegisterDomainEventHandlers { get; set; } = true;

    /// <summary>
    /// Adds an assembly to the explicit scan set.
    /// </summary>
    public TcjDependencyInjectionOptions AddAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        if (assembly.IsDynamic)
        {
            throw new ArgumentException(
                "Dynamic assemblies cannot be used for dependency scanning.",
                nameof(assembly));
        }

        _assemblies.Add(assembly);
        return this;
    }

    /// <summary>
    /// Adds multiple assemblies to the explicit scan set.
    /// </summary>
    public TcjDependencyInjectionOptions AddAssemblies(
        IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        foreach (var assembly in assemblies)
        {
            AddAssembly(assembly);
        }

        return this;
    }

    /// <summary>
    /// Adds the assembly containing <typeparamref name="TMarker"/> to the scan set.
    /// </summary>
    public TcjDependencyInjectionOptions AddAssemblyContaining<TMarker>() =>
        AddAssembly(typeof(TMarker).Assembly);
}
