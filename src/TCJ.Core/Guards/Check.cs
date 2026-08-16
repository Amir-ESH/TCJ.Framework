using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace TCJ.Core.Guards;

/// <summary>
/// Provides guard clauses that return the validated value or throw an
/// appropriate argument exception.
/// </summary>
[DebuggerStepThrough]
public static class Check
{
    /// <summary>
    /// Ensures that the value is not null.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="parameterName">The optional parameter name used by the thrown exception.</param>
    /// <returns>The result of the operation.</returns>
    public static T NotNull<T>(
        [NotNull] this T? value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        return value;
    }
    /// <summary>
    /// Ensures that the supplied value is not null or empty.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="parameterName">The optional parameter name used by the thrown exception.</param>
    /// <returns>The result of the operation.</returns>

    public static string NotNullOrEmpty(
        [NotNull] this string? value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, parameterName);
        return value;
    }
    /// <summary>
    /// Ensures that the string is not null, empty, or whitespace.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="parameterName">The optional parameter name used by the thrown exception.</param>
    /// <returns>The result of the operation.</returns>

    public static string NotNullOrWhiteSpace(
        [NotNull] this string? value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }
    /// <summary>
    /// Ensures that the supplied value is not null or empty.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="parameterName">The optional parameter name used by the thrown exception.</param>
    /// <returns>The result of the operation.</returns>

    public static IReadOnlyCollection<T> NotNullOrEmpty<T>(
        [NotNull] this IReadOnlyCollection<T>? value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (value.Count == 0)
        {
            throw new ArgumentException("Collection cannot be empty.", parameterName);
        }

        return value;
    }
    /// <summary>
    /// Ensures that the string length falls within the inclusive range.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="minimumLength">The inclusive minimum length.</param>
    /// <param name="maximumLength">The inclusive maximum length.</param>
    /// <param name="parameterName">The optional parameter name used by the thrown exception.</param>
    /// <returns>The result of the operation.</returns>

    public static string LengthBetween(
        this string value,
        int minimumLength,
        int maximumLength,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (minimumLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumLength));
        }

        if (maximumLength < minimumLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumLength),
                "Maximum length cannot be less than minimum length.");
        }

        if (value.Length < minimumLength || value.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value.Length,
                $"Length must be between {minimumLength} and {maximumLength}.");
        }

        return value;
    }
    /// <summary>
    /// Ensures that the value is greater than zero.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="parameterName">The optional parameter name used by the thrown exception.</param>
    /// <returns>The result of the operation.</returns>

    public static T Positive<T>(
        this T value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
        where T : INumber<T>
    {
        if (value <= T.Zero)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Value must be greater than zero.");
        }

        return value;
    }
    /// <summary>
    /// Ensures that the value falls within the inclusive range.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="minimumValue">The inclusive minimum value.</param>
    /// <param name="maximumValue">The inclusive maximum value.</param>
    /// <param name="parameterName">The optional parameter name used by the thrown exception.</param>
    /// <returns>The result of the operation.</returns>

    public static T InRange<T>(
        this T value,
        T minimumValue,
        T maximumValue,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
        where T : IComparable<T>
    {
        if (minimumValue.CompareTo(maximumValue) > 0)
        {
            throw new ArgumentException(
                "Minimum value cannot be greater than maximum value.",
                nameof(minimumValue));
        }

        if (value.CompareTo(minimumValue) < 0 || value.CompareTo(maximumValue) > 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Value must be between {minimumValue} and {maximumValue}.");
        }

        return value;
    }
    /// <summary>
    /// Ensures that the value is not its default value.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="parameterName">The optional parameter name used by the thrown exception.</param>
    /// <returns>The result of the operation.</returns>

    public static T NotDefault<T>(
        [NotNull] this T? value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (EqualityComparer<T>.Default.Equals(value, default!))
        {
            throw new ArgumentException("Value cannot be the default value.", parameterName);
        }

        return value;
    }
    /// <summary>
    /// Ensures that the supplied type is assignable to the requested base type.
    /// </summary>
    /// <typeparam name="TBaseType">The required base type.</typeparam>
    /// <param name="type">The type to validate.</param>
    /// <param name="parameterName">The optional parameter name used by the thrown exception.</param>
    /// <returns>The result of the operation.</returns>

    public static Type AssignableTo<TBaseType>(
        [NotNull] this Type? type,
        [CallerArgumentExpression(nameof(type))] string? parameterName = null)
    {
        ArgumentNullException.ThrowIfNull(type, parameterName);

        if (!typeof(TBaseType).IsAssignableFrom(type))
        {
            throw new ArgumentException(
                $"Type '{type.AssemblyQualifiedName}' must be assignable to '{typeof(TBaseType).AssemblyQualifiedName}'.",
                parameterName);
        }

        return type;
    }
}
