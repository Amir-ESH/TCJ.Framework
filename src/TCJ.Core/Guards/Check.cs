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

    public static string NotNullOrEmpty(
        [NotNull] this string? value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, parameterName);
        return value;
    }

    public static string NotNullOrWhiteSpace(
        [NotNull] this string? value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }

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
