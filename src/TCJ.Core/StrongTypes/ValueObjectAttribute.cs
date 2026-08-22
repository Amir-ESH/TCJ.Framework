namespace TCJ.Core.StrongTypes;

/// <summary>
/// Marks a type as a strongly typed value object contract.
/// </summary>
/// <typeparam name="TValue">The underlying value type.</typeparam>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ValueObjectAttribute<TValue> : Attribute;
