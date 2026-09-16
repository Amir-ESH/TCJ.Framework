using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace TCJ.Messaging.Contracts.Validation;

internal static class JsonSchemaInstanceValidator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static IReadOnlyList<string> Validate(JsonElement instance, JsonElement schema)
    {
        var errors = new List<string>();
        ValidateCore(instance, schema, schema, "$", errors, 0);
        return errors;
    }

    private static void ValidateCore(JsonElement instance, JsonElement schema, JsonElement root, string path, List<string> errors, int depth)
    {
        if (depth > 128)
        {
            errors.Add($"{path}: schema validation depth exceeded 128.");
            return;
        }

        if (schema.ValueKind == JsonValueKind.True)
            return;
        if (schema.ValueKind == JsonValueKind.False)
        {
            errors.Add($"{path}: schema rejects this value.");
            return;
        }
        if (schema.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}: malformed schema node.");
            return;
        }

        if (schema.TryGetProperty("$ref", out JsonElement reference) && reference.ValueKind == JsonValueKind.String)
        {
            if (!TryResolveLocalReference(root, reference.GetString()!, out JsonElement resolved))
            {
                errors.Add($"{path}: unsupported or unresolved schema reference.");
                return;
            }
            ValidateCore(instance, resolved, root, path, errors, depth + 1);
        }

        ValidateCombinators(instance, schema, root, path, errors, depth);
        ValidateType(instance, schema, path, errors);
        ValidateEnumConst(instance, schema, path, errors);
        ValidateNumber(instance, schema, path, errors);
        ValidateString(instance, schema, path, errors);
        ValidateArray(instance, schema, root, path, errors, depth);
        ValidateObject(instance, schema, root, path, errors, depth);
    }

    private static void ValidateCombinators(JsonElement instance, JsonElement schema, JsonElement root, string path, List<string> errors, int depth)
    {
        if (schema.TryGetProperty("allOf", out JsonElement allOf) && allOf.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement candidate in allOf.EnumerateArray())
                ValidateCore(instance, candidate, root, path, errors, depth + 1);
        }

        if (schema.TryGetProperty("anyOf", out JsonElement anyOf) && anyOf.ValueKind == JsonValueKind.Array)
        {
            bool anyValid = anyOf.EnumerateArray().Any(candidate => IsValid(instance, candidate, root, depth + 1));
            if (!anyValid)
                errors.Add($"{path}: value does not satisfy any allowed schema branch.");
        }

        if (schema.TryGetProperty("oneOf", out JsonElement oneOf) && oneOf.ValueKind == JsonValueKind.Array)
        {
            int validCount = oneOf.EnumerateArray().Count(candidate => IsValid(instance, candidate, root, depth + 1));
            if (validCount != 1)
                errors.Add($"{path}: value must satisfy exactly one schema branch.");
        }

        if (schema.TryGetProperty("not", out JsonElement not) && IsValid(instance, not, root, depth + 1))
            errors.Add($"{path}: value matches a prohibited schema branch.");
    }

    private static bool IsValid(JsonElement instance, JsonElement schema, JsonElement root, int depth)
    {
        var errors = new List<string>();
        ValidateCore(instance, schema, root, "$", errors, depth);
        return errors.Count == 0;
    }

    private static void ValidateType(JsonElement instance, JsonElement schema, string path, List<string> errors)
    {
        if (!schema.TryGetProperty("type", out JsonElement type))
            return;
        bool valid = type.ValueKind switch
        {
            JsonValueKind.String => TypeMatches(instance, type.GetString()!),
            JsonValueKind.Array => type.EnumerateArray().Any(candidate => candidate.ValueKind == JsonValueKind.String && TypeMatches(instance, candidate.GetString()!)),
            _ => false
        };
        if (!valid)
            errors.Add($"{path}: JSON value kind '{instance.ValueKind}' is not allowed by schema type.");
    }

    private static bool TypeMatches(JsonElement instance, string type) => type switch
    {
        "null" => instance.ValueKind == JsonValueKind.Null,
        "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => instance.ValueKind == JsonValueKind.Object,
        "array" => instance.ValueKind == JsonValueKind.Array,
        "string" => instance.ValueKind == JsonValueKind.String,
        "number" => instance.ValueKind == JsonValueKind.Number,
        "integer" => instance.ValueKind == JsonValueKind.Number && IsInteger(instance),
        _ => false
    };

    private static bool IsInteger(JsonElement value)
    {
        if (value.TryGetInt64(out _))
            return true;
        return value.TryGetDecimal(out decimal number) && number == decimal.Truncate(number);
    }

    private static void ValidateEnumConst(JsonElement instance, JsonElement schema, string path, List<string> errors)
    {
        if (schema.TryGetProperty("const", out JsonElement constValue) && !JsonElement.DeepEquals(instance, constValue))
            errors.Add($"{path}: value does not match schema const.");
        if (schema.TryGetProperty("enum", out JsonElement enumValue) && enumValue.ValueKind == JsonValueKind.Array &&
            !enumValue.EnumerateArray().Any(candidate => JsonElement.DeepEquals(instance, candidate)))
            errors.Add($"{path}: value is not present in schema enum.");
    }

    private static void ValidateNumber(JsonElement instance, JsonElement schema, string path, List<string> errors)
    {
        if (instance.ValueKind != JsonValueKind.Number || !instance.TryGetDecimal(out decimal number))
            return;
        if (TryDecimal(schema, "minimum", out decimal minimum) && number < minimum)
            errors.Add($"{path}: number is below minimum.");
        if (TryDecimal(schema, "maximum", out decimal maximum) && number > maximum)
            errors.Add($"{path}: number is above maximum.");
        if (TryDecimal(schema, "exclusiveMinimum", out decimal exclusiveMinimum) && number <= exclusiveMinimum)
            errors.Add($"{path}: number does not satisfy exclusiveMinimum.");
        if (TryDecimal(schema, "exclusiveMaximum", out decimal exclusiveMaximum) && number >= exclusiveMaximum)
            errors.Add($"{path}: number does not satisfy exclusiveMaximum.");
        if (TryDecimal(schema, "multipleOf", out decimal multipleOf) && multipleOf != 0 && number % multipleOf != 0)
            errors.Add($"{path}: number does not satisfy multipleOf.");
    }

    private static void ValidateString(JsonElement instance, JsonElement schema, string path, List<string> errors)
    {
        if (instance.ValueKind != JsonValueKind.String)
            return;
        string value = instance.GetString()!;
        if (TryInt32(schema, "minLength", out int minLength) && value.EnumerateRunes().Count() < minLength)
            errors.Add($"{path}: string is shorter than minLength.");
        if (TryInt32(schema, "maxLength", out int maxLength) && value.EnumerateRunes().Count() > maxLength)
            errors.Add($"{path}: string is longer than maxLength.");
        if (schema.TryGetProperty("pattern", out JsonElement pattern) && pattern.ValueKind == JsonValueKind.String)
        {
            try
            {
                if (!Regex.IsMatch(value, pattern.GetString()!, RegexOptions.CultureInvariant, RegexTimeout))
                    errors.Add($"{path}: string does not match schema pattern.");
            }
            catch (ArgumentException)
            {
                errors.Add($"{path}: schema pattern is invalid.");
            }
            catch (RegexMatchTimeoutException)
            {
                errors.Add($"{path}: schema pattern validation timed out.");
            }
        }
    }

    private static void ValidateArray(JsonElement instance, JsonElement schema, JsonElement root, string path, List<string> errors, int depth)
    {
        if (instance.ValueKind != JsonValueKind.Array)
            return;
        int length = instance.GetArrayLength();
        if (TryInt32(schema, "minItems", out int minItems) && length < minItems)
            errors.Add($"{path}: array contains fewer than minItems.");
        if (TryInt32(schema, "maxItems", out int maxItems) && length > maxItems)
            errors.Add($"{path}: array contains more than maxItems.");
        if (schema.TryGetProperty("items", out JsonElement items))
        {
            int index = 0;
            foreach (JsonElement item in instance.EnumerateArray())
            {
                ValidateCore(item, items, root, $"{path}[{index}]", errors, depth + 1);
                index++;
            }
        }
    }

    private static void ValidateObject(JsonElement instance, JsonElement schema, JsonElement root, string path, List<string> errors, int depth)
    {
        if (instance.ValueKind != JsonValueKind.Object)
            return;

        Dictionary<string, JsonElement> properties = schema.TryGetProperty("properties", out JsonElement propertiesElement) && propertiesElement.ValueKind == JsonValueKind.Object
            ? propertiesElement.EnumerateObject().ToDictionary(static property => property.Name, static property => property.Value, StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (schema.TryGetProperty("required", out JsonElement required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement requiredName in required.EnumerateArray())
            {
                if (requiredName.ValueKind == JsonValueKind.String && !instance.TryGetProperty(requiredName.GetString()!, out _))
                    errors.Add($"{path}: required property '{requiredName.GetString()}' is missing.");
            }
        }

        foreach (JsonProperty property in instance.EnumerateObject())
        {
            if (properties.TryGetValue(property.Name, out JsonElement propertySchema))
            {
                ValidateCore(property.Value, propertySchema, root, path + "." + property.Name, errors, depth + 1);
                continue;
            }

            if (!schema.TryGetProperty("additionalProperties", out JsonElement additional))
                continue;
            if (additional.ValueKind == JsonValueKind.False)
                errors.Add($"{path}: additional property '{property.Name}' is not allowed.");
            else if (additional.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False)
                ValidateCore(property.Value, additional, root, path + "." + property.Name, errors, depth + 1);
        }
    }

    private static bool TryResolveLocalReference(JsonElement root, string reference, out JsonElement resolved)
    {
        resolved = root;
        if (reference == "#")
            return true;
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
            return false;
        foreach (string rawPart in reference[2..].Split('/'))
        {
            string part = rawPart.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (resolved.ValueKind != JsonValueKind.Object || !resolved.TryGetProperty(part, out JsonElement next))
                return false;
            resolved = next;
        }
        return true;
    }

    private static bool TryDecimal(JsonElement schema, string propertyName, out decimal value)
    {
        value = default;
        return schema.TryGetProperty(propertyName, out JsonElement element) && element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value);
    }

    private static bool TryInt32(JsonElement schema, string propertyName, out int value)
    {
        value = default;
        return schema.TryGetProperty(propertyName, out JsonElement element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }
}
