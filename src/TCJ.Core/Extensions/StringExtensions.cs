using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TCJ.Core.Extensions;
/// <summary>
/// Provides string normalization, casing, truncation, and matching helpers.
/// </summary>

public static partial class StringExtensions
{
    /// <summary>
    /// Ensures that the string ends with the specified character.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="suffix">The suffix to apply.</param>
    /// <returns>The resulting value.</returns>
    public static string EnsureEndsWith(this string value, char suffix)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.EndsWith(suffix) ? value : string.Concat(value, suffix.ToString());
    }
    /// <summary>
    /// Ensures that the string starts with the specified character.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="prefix">The prefix to apply.</param>
    /// <returns>The resulting value.</returns>

    public static string EnsureStartsWith(this string value, char prefix)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.StartsWith(prefix) ? value : string.Concat(prefix.ToString(), value);
    }
    /// <summary>
    /// Normalizes line endings to the current environment newline sequence.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <returns>The resulting value.</returns>

    public static string NormalizeLineEndings(this string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ReplaceLineEndings(Environment.NewLine);
    }
    /// <summary>
    /// Finds the zero-based index of the requested occurrence of a character.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="character">The character to locate.</param>
    /// <param name="occurrence">The one-based occurrence to locate.</param>
    /// <returns>The result of the operation.</returns>

    public static int NthIndexOf(this string value, char character, int occurrence)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (occurrence <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(occurrence),
                occurrence,
                "Occurrence must be greater than zero.");
        }

        var count = 0;

        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != character)
            {
                continue;
            }

            count++;

            if (count == occurrence)
            {
                return index;
            }
        }

        return -1;
    }
    /// <summary>
    /// Removes the first matching suffix from the string.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="suffixes">The candidate suffixes.</param>
    /// <returns>The resulting value.</returns>

    public static string RemoveSuffix(this string value, params string[] suffixes)
        => value.RemoveSuffix(StringComparison.Ordinal, suffixes);
    /// <summary>
    /// Removes the first matching suffix from the string.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="comparisonType">The string comparison rules to use.</param>
    /// <param name="suffixes">The candidate suffixes.</param>
    /// <returns>The resulting value.</returns>

    public static string RemoveSuffix(
        this string value,
        StringComparison comparisonType,
        params string[] suffixes)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(suffixes);

        foreach (var suffix in suffixes)
        {
            ArgumentNullException.ThrowIfNull(suffix);

            if (value.EndsWith(suffix, comparisonType))
            {
                return value[..^suffix.Length];
            }
        }

        return value;
    }
    /// <summary>
    /// Removes the first matching prefix from the string.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="prefixes">The candidate prefixes.</param>
    /// <returns>The resulting value.</returns>

    public static string RemovePrefix(this string value, params string[] prefixes)
        => value.RemovePrefix(StringComparison.Ordinal, prefixes);
    /// <summary>
    /// Removes the first matching prefix from the string.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="comparisonType">The string comparison rules to use.</param>
    /// <param name="prefixes">The candidate prefixes.</param>
    /// <returns>The resulting value.</returns>

    public static string RemovePrefix(
        this string value,
        StringComparison comparisonType,
        params string[] prefixes)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(prefixes);

        foreach (var prefix in prefixes)
        {
            ArgumentNullException.ThrowIfNull(prefix);

            if (value.StartsWith(prefix, comparisonType))
            {
                return value[prefix.Length..];
            }
        }

        return value;
    }
    /// <summary>
    /// Replaces the first matching occurrence in the string.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="search">The text to search for.</param>
    /// <param name="replacement">The replacement text.</param>
    /// <param name="comparisonType">The string comparison rules to use.</param>
    /// <returns>The resulting value.</returns>

    public static string ReplaceFirst(
        this string value,
        string search,
        string replacement,
        StringComparison comparisonType = StringComparison.Ordinal)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrEmpty(search);
        ArgumentNullException.ThrowIfNull(replacement);

        var index = value.IndexOf(search, comparisonType);

        if (index < 0)
        {
            return value;
        }

        return string.Concat(
            value.AsSpan(0, index),
            replacement,
            value.AsSpan(index + search.Length));
    }
    /// <summary>
    /// Splits the string into individual lines.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="options">The split options to apply.</param>
    /// <returns>The resulting value.</returns>

    public static string[] SplitLines(
        this string value,
        StringSplitOptions options = StringSplitOptions.None)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ReplaceLineEndings("\n").Split('\n', options);
    }
    /// <summary>
    /// Converts the string to camel case.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="normalizeAcronyms">Whether acronym casing should be normalized.</param>
    /// <returns>The resulting value.</returns>

    [return: NotNullIfNotNull(nameof(value))]
    public static string? ToCamelCase(
        this string? value,
        bool normalizeAcronyms = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (normalizeAcronyms && IsAllUpperCase(value))
        {
            return value.ToLowerInvariant();
        }

        if (char.IsLower(value[0]))
        {
            return value;
        }

        return string.Concat(
            char.ToLowerInvariant(value[0]).ToString(),
            value.AsSpan(1));
    }
    /// <summary>
    /// Converts the string to Pascal case.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <returns>The resulting value.</returns>

    [return: NotNullIfNotNull(nameof(value))]
    public static string? ToPascalCase(this string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || char.IsUpper(value[0]))
        {
            return value;
        }

        return string.Concat(
            char.ToUpperInvariant(value[0]).ToString(),
            value.AsSpan(1));
    }
    /// <summary>
    /// Converts the string to sentence case.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <returns>The resulting value.</returns>

    [return: NotNullIfNotNull(nameof(value))]
    public static string? ToSentenceCase(this string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return SentenceBoundaryRegex().Replace(
            value,
            match => $" {char.ToLowerInvariant(match.Value[0])}");
    }
    /// <summary>
    /// Converts the string to kebab case.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <returns>The resulting value.</returns>

    [return: NotNullIfNotNull(nameof(value))]
    public static string? ToKebabCase(this string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? value
            : JsonNamingPolicy.KebabCaseLower.ConvertName(value);
    }
    /// <summary>
    /// Converts the string to snake case.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <returns>The resulting value.</returns>

    [return: NotNullIfNotNull(nameof(value))]
    public static string? ToSnakeCase(this string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? value
            : JsonNamingPolicy.SnakeCaseLower.ConvertName(value);
    }
    /// <summary>
    /// Parses the string as the requested enumeration type.
    /// </summary>
    /// <typeparam name="TEnum">The enumeration type.</typeparam>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="ignoreCase">Whether parsing should ignore character casing.</param>
    /// <returns>The resulting value.</returns>

    public static TEnum ToEnum<TEnum>(
        this string value,
        bool ignoreCase = true)
        where TEnum : struct, Enum
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Enum.Parse<TEnum>(value, ignoreCase);
    }
    /// <summary>
    /// Truncates the value to the requested maximum precision or length.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="maxLength">The maximum permitted length.</param>
    /// <returns>The resulting value.</returns>

    [return: NotNullIfNotNull(nameof(value))]
    public static string? Truncate(this string? value, int maxLength)
        => value.Truncate(maxLength, string.Empty);
    /// <summary>
    /// Truncates the value to the requested maximum precision or length.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="maxLength">The maximum permitted length.</param>
    /// <param name="suffix">The suffix to apply.</param>
    /// <returns>The resulting value.</returns>

    [return: NotNullIfNotNull(nameof(value))]
    public static string? Truncate(
        this string? value,
        int maxLength,
        string suffix)
    {
        if (value is null)
        {
            return null;
        }

        ArgumentNullException.ThrowIfNull(suffix);

        if (maxLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength));
        }

        if (suffix.Length > maxLength)
        {
            throw new ArgumentException(
                "Suffix cannot be longer than the maximum length.",
                nameof(suffix));
        }

        if (value.Length <= maxLength)
        {
            return value;
        }

        return string.Concat(
            value.AsSpan(0, maxLength - suffix.Length),
            suffix);
    }
    /// <summary>
    /// Truncates the beginning of the string to fit the requested maximum length.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="maxLength">The maximum permitted length.</param>
    /// <returns>The resulting value.</returns>

    [return: NotNullIfNotNull(nameof(value))]
    public static string? TruncateFromStart(this string? value, int maxLength)
    {
        if (value is null)
        {
            return null;
        }

        if (maxLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength));
        }

        return value.Length <= maxLength ? value : value[^maxLength..];
    }
    /// <summary>
    /// Collapses repeated whitespace and trims the resulting string.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <returns>The resulting value.</returns>

    public static string NormalizeWhitespace(this string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : WhitespaceRegex().Replace(value.Trim(), " ");
    }
    /// <summary>
    /// Returns a fallback when the string has no value.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="defaultValue">The fallback value.</param>
    /// <returns>The resulting value.</returns>

    public static string WithDefault(this string? value, string defaultValue)
    {
        ArgumentNullException.ThrowIfNull(defaultValue);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }
    /// <summary>
    /// Returns a fallback when the string is null or empty.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="defaultValue">The fallback value.</param>
    /// <returns>The resulting value.</returns>

    public static string WithDefaultIfEmpty(
        this string? value,
        string defaultValue)
    {
        ArgumentNullException.ThrowIfNull(defaultValue);
        return string.IsNullOrEmpty(value) ? defaultValue : value;
    }
    /// <summary>
    /// Determines whether the string contains a non-whitespace value.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <returns>true when the condition is satisfied; otherwise, false.</returns>

    public static bool HasValue([NotNullWhen(true)] this string? value)
        => !string.IsNullOrWhiteSpace(value);
    /// <summary>
    /// Returns null when the string is null, empty, or whitespace.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <returns>The resulting value.</returns>

    public static string? NullIfWhiteSpace(this string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
    /// <summary>
    /// Returns null when the string is empty.
    /// </summary>
    /// <param name="value">The value to inspect or transform.</param>
    /// <returns>The resulting value.</returns>

    public static string? NullIfEmpty(this string? value)
        => string.IsNullOrEmpty(value) ? null : value;

    private static bool IsAllUpperCase(string value)
        => value.All(character => !char.IsLetter(character) || char.IsUpper(character));

    [GeneratedRegex("(?<=[a-z0-9])[A-Z]")]
    private static partial Regex SentenceBoundaryRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}
