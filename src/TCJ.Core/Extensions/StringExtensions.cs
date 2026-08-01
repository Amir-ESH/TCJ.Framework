using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TCJ.Core.Extensions;

public static partial class StringExtensions
{
    public static string EnsureEndsWith(this string value, char suffix)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.EndsWith(suffix) ? value : string.Concat(value, suffix.ToString());
    }

    public static string EnsureStartsWith(this string value, char prefix)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.StartsWith(prefix) ? value : string.Concat(prefix.ToString(), value);
    }

    public static string NormalizeLineEndings(this string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ReplaceLineEndings(Environment.NewLine);
    }

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

    public static string RemoveSuffix(this string value, params string[] suffixes)
        => value.RemoveSuffix(StringComparison.Ordinal, suffixes);

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

    public static string RemovePrefix(this string value, params string[] prefixes)
        => value.RemovePrefix(StringComparison.Ordinal, prefixes);

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

    public static string[] SplitLines(
        this string value,
        StringSplitOptions options = StringSplitOptions.None)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ReplaceLineEndings("\n").Split('\n', options);
    }

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

    [return: NotNullIfNotNull(nameof(value))]
    public static string? ToKebabCase(this string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? value
            : JsonNamingPolicy.KebabCaseLower.ConvertName(value);
    }

    [return: NotNullIfNotNull(nameof(value))]
    public static string? ToSnakeCase(this string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? value
            : JsonNamingPolicy.SnakeCaseLower.ConvertName(value);
    }

    public static TEnum ToEnum<TEnum>(
        this string value,
        bool ignoreCase = true)
        where TEnum : struct, Enum
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Enum.Parse<TEnum>(value, ignoreCase);
    }

    [return: NotNullIfNotNull(nameof(value))]
    public static string? Truncate(this string? value, int maxLength)
        => value.Truncate(maxLength, string.Empty);

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

    public static string NormalizeWhitespace(this string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : WhitespaceRegex().Replace(value.Trim(), " ");
    }

    public static string WithDefault(this string? value, string defaultValue)
    {
        ArgumentNullException.ThrowIfNull(defaultValue);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    public static string WithDefaultIfEmpty(
        this string? value,
        string defaultValue)
    {
        ArgumentNullException.ThrowIfNull(defaultValue);
        return string.IsNullOrEmpty(value) ? defaultValue : value;
    }

    public static bool HasValue([NotNullWhen(true)] this string? value)
        => !string.IsNullOrWhiteSpace(value);

    public static string? NullIfWhiteSpace(this string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    public static string? NullIfEmpty(this string? value)
        => string.IsNullOrEmpty(value) ? null : value;

    private static bool IsAllUpperCase(string value)
        => value.All(character => !char.IsLetter(character) || char.IsUpper(character));

    [GeneratedRegex("(?<=[a-z0-9])[A-Z]")]
    private static partial Regex SentenceBoundaryRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}
