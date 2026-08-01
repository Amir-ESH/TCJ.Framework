namespace TCJ.DependencyInjection.Lifetimes;

/// <summary>
/// Registers a concrete type through its implemented service interfaces with a transient lifetime.
/// </summary>
public interface ITransientDependency : IDependency;
