using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TCJ.Messaging.Contracts.Schema;

internal static class JsonSchemaCanonicalizer
{
    public static byte[] Canonicalize(JsonNode schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        using JsonDocument document = JsonDocument.Parse(schema.ToJsonString());
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteElement(writer, document.RootElement, null);
        }
        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] CanonicalizeUtf8(ReadOnlySpan<byte> json)
    {
        var options = new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 128
        };
        using JsonDocument document = JsonDocument.Parse(json.ToArray(), options);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteElement(writer, document.RootElement, null);
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element, string? parentProperty)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value, property.Name);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                if (string.Equals(parentProperty, "required", StringComparison.Ordinal) &&
                    element.EnumerateArray().All(static item => item.ValueKind == JsonValueKind.String))
                {
                    foreach (string value in element.EnumerateArray()
                        .Select(static item => item.GetString()!)
                        .Order(StringComparer.Ordinal))
                    {
                        writer.WriteStringValue(value);
                    }
                }
                else
                {
                    foreach (JsonElement item in element.EnumerateArray())
                        WriteElement(writer, item, null);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(NormalizeNumber(element.GetRawText()), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException($"Unsupported JSON token '{element.ValueKind}' in canonical schema.");
        }
    }

    private static string NormalizeNumber(string raw)
    {
        int offset = raw[0] == '-' ? 1 : 0;
        bool negative = offset == 1;
        int exponentMarker = raw.IndexOfAny(['e', 'E'], offset);
        ReadOnlySpan<char> mantissa = exponentMarker >= 0 ? raw.AsSpan(offset, exponentMarker - offset) : raw.AsSpan(offset);
        ReadOnlySpan<char> exponentText = exponentMarker >= 0 ? raw.AsSpan(exponentMarker + 1) : default;

        int decimalPoint = mantissa.IndexOf('.');
        ReadOnlySpan<char> integerPart = decimalPoint >= 0 ? mantissa[..decimalPoint] : mantissa;
        ReadOnlySpan<char> fractionPart = decimalPoint >= 0 ? mantissa[(decimalPoint + 1)..] : default;
        string digits = string.Concat(integerPart, fractionPart).TrimStart('0');
        if (digits.Length == 0)
            return "0";

        BigInteger exponent = exponentText.IsEmpty
            ? BigInteger.Zero
            : BigInteger.Parse(exponentText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        exponent -= fractionPart.Length;

        int trailingZeros = 0;
        for (int index = digits.Length - 1; index > 0 && digits[index] == '0'; index--)
            trailingZeros++;
        if (trailingZeros > 0)
        {
            digits = digits[..^trailingZeros];
            exponent += trailingZeros;
        }

        string sign = negative ? "-" : string.Empty;
        return exponent.IsZero
            ? sign + digits
            : string.Concat(sign, digits, "e", exponent.ToString(CultureInfo.InvariantCulture));
    }
}
