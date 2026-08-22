namespace TCJ.Core.StrongTypes;

/// <summary>
/// Defines a runtime-neutral strongly typed value contract.
/// </summary>
/// <typeparam name="TValue">The underlying value type.</typeparam>
public interface IStronglyTypedValue<out TValue>
{
    /// <summary>
    /// Gets the underlying value.
    /// </summary>
    TValue Value { get; }
}
