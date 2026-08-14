using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace TCJ.EntityFrameworkCore.Searching.Internal;

internal static class EntityKeyValueConverter
{
    [RequiresUnreferencedCode("Entity search converts runtime EF model types with TypeDescriptor. Native AOT consumers should use statically typed repository or DbContext queries that EF tooling can precompile.")]
    public static object ConvertFromInvariantString(
        string value,
        Type targetType,
        string entityName,
        string propertyName)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(targetType);

        Type effectiveType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        try
        {
            if (effectiveType == typeof(string))
            {
                return value;
            }

            if (effectiveType == typeof(Guid))
            {
                return Guid.Parse(value);
            }

            if (effectiveType == typeof(DateTimeOffset))
            {
                return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.None);
            }

            if (effectiveType == typeof(DateTime))
            {
                return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            }

            if (effectiveType == typeof(DateOnly))
            {
                return DateOnly.Parse(value, CultureInfo.InvariantCulture);
            }

            if (effectiveType == typeof(TimeOnly))
            {
                return TimeOnly.Parse(value, CultureInfo.InvariantCulture);
            }

            if (effectiveType == typeof(TimeSpan))
            {
                return TimeSpan.Parse(value, CultureInfo.InvariantCulture);
            }

            if (effectiveType == typeof(byte[]))
            {
                return System.Convert.FromBase64String(value);
            }

            if (effectiveType.IsEnum)
            {
                return Enum.Parse(effectiveType, value, ignoreCase: true);
            }

            TypeConverter converter = TypeDescriptor.GetConverter(effectiveType);

            if (converter.CanConvertFrom(typeof(string)))
            {
                return converter.ConvertFromInvariantString(value)
                    ?? throw new InvalidOperationException(
                        $"The converter for '{effectiveType.FullName}' returned null.");
            }

            return System.Convert.ChangeType(value, effectiveType, CultureInfo.InvariantCulture)
                ?? throw new InvalidOperationException(
                    $"The value could not be converted to '{effectiveType.FullName}'.");
        }
        catch (Exception exception) when (exception is ArgumentException
                                               or FormatException
                                               or InvalidCastException
                                               or NotSupportedException
                                               or OverflowException)
        {
            throw new ArgumentException(
                $"The value '{value}' is not a valid key for property '{propertyName}' " +
                $"on entity '{entityName}'. Expected CLR type: '{effectiveType.FullName}'.",
                nameof(value),
                exception);
        }
    }
}
