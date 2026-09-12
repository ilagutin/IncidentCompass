using System.Text.Json;
using IncidentCompass.Application.Core.Text;

namespace IncidentCompass.Application.Investigation.Jobs;

internal static class AnalysisWorkerOutputSchemaValidator
{
    internal const int MaxReportedViolations = 20;

    private const string ObjectSchemaType = "object";
    private const string ArraySchemaType = "array";
    private const string StringSchemaType = "string";
    private const string BooleanSchemaType = "boolean";
    private const string NumberSchemaType = "number";

    /// <summary>
    /// The schema <c>type</c> values <c>ValidateElement</c> knows how to evaluate, built from the same
    /// tokens its switch matches. A type outside this set leaves the worker loop as an
    /// <see cref="InvalidOperationException" /> rather than as a correctable validation failure, so the
    /// configuration tests that guard shipped and fixture schemas read the set from here through
    /// <see cref="IsSupportedSchemaType" /> instead of restating it.
    /// </summary>
    private static readonly HashSet<string> SupportedSchemaTypes = new(StringComparer.Ordinal)
    {
        ObjectSchemaType,
        ArraySchemaType,
        StringSchemaType,
        BooleanSchemaType,
        NumberSchemaType
    };

    internal static bool IsSupportedSchemaType(string schemaType) => SupportedSchemaTypes.Contains(schemaType);

    public static string Validate(string content, string outputSchema, string roleName)
    {
        using var schemaDocument = ParseSchema(outputSchema, roleName);
        if (!StrictJsonOutputNormalizer.TryNormalize(content, out var normalizedJson))
        {
            throw InvalidJson(roleName);
        }

        JsonDocument outputDocument;
        try
        {
            outputDocument = JsonDocument.Parse(normalizedJson);
        }
        catch (JsonException)
        {
            throw InvalidJson(roleName);
        }

        using (outputDocument)
        {
            var violations = new List<string>();
            var violationsTruncated = false;
            ValidateElement(
                outputDocument.RootElement,
                schemaDocument.RootElement,
                "output",
                WorkerOutputDiagnosticLabels.Output(roleName),
                WorkerOutputDiagnosticLabels.Schema(roleName),
                violations,
                ref violationsTruncated);
            if (violations.Count > 0)
            {
                throw new WorkerOutputValidationException(violations, violationsTruncated);
            }
        }

        return normalizedJson;
    }

    private static JsonDocument ParseSchema(string outputSchema, string roleName)
    {
        try
        {
            return JsonDocument.Parse(outputSchema);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                WorkerOutputDiagnosticLabels.Schema(roleName) + " is not valid JSON.");
        }
    }

    private static void ValidateElement(
        JsonElement value,
        JsonElement schema,
        string path,
        string outputLabel,
        string schemaLabel,
        List<string> violations,
        ref bool violationsTruncated)
    {
        if (violationsTruncated)
        {
            return;
        }

        var expectedType = ReadSchemaType(schema, path, schemaLabel);
        switch (expectedType)
        {
            case ObjectSchemaType:
                ValidateObject(value, schema, path, outputLabel, schemaLabel, violations, ref violationsTruncated);
                break;
            case ArraySchemaType:
                ValidateArray(value, schema, path, outputLabel, schemaLabel, violations, ref violationsTruncated);
                break;
            case StringSchemaType:
                ValidateString(value, schema, path, outputLabel, violations, ref violationsTruncated);
                break;
            case BooleanSchemaType:
                if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                {
                    AddViolation(violations, ref violationsTruncated, Invalid(path, BooleanSchemaType, outputLabel));
                }

                break;
            case NumberSchemaType:
                if (value.ValueKind != JsonValueKind.Number)
                {
                    AddViolation(violations, ref violationsTruncated, Invalid(path, NumberSchemaType, outputLabel));
                }

                break;
            default:
                throw new InvalidOperationException($"{schemaLabel} type '{expectedType}' at {path} is not supported.");
        }
    }

    private static void ValidateObject(
        JsonElement value,
        JsonElement schema,
        string path,
        string outputLabel,
        string schemaLabel,
        List<string> violations,
        ref bool violationsTruncated)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            AddViolation(violations, ref violationsTruncated, Invalid(path, ObjectSchemaType, outputLabel));
            return;
        }

        var hasProperties = TryGetObject(schema, "properties", out var properties);
        ValidateRequiredProperties(value, schema, path, outputLabel, violations, ref violationsTruncated);
        if (IsAdditionalPropertiesFalse(schema))
        {
            var unsupportedPropertyCount = value.EnumerateObject().Count(property =>
                !hasProperties || !properties.TryGetProperty(property.Name, out _));
            if (unsupportedPropertyCount > 0)
            {
                AddViolation(
                    violations,
                    ref violationsTruncated,
                    $"{outputLabel} contains {unsupportedPropertyCount} unsupported properties at {path}.");
            }
        }

        if (!hasProperties)
        {
            return;
        }

        foreach (var propertySchema in properties.EnumerateObject())
        {
            if (violationsTruncated)
            {
                return;
            }

            if (value.TryGetProperty(propertySchema.Name, out var propertyValue))
            {
                ValidateElement(
                    propertyValue,
                    propertySchema.Value,
                    path + "." + propertySchema.Name,
                    outputLabel,
                    schemaLabel,
                    violations,
                    ref violationsTruncated);
            }
        }
    }

    private static void ValidateArray(
        JsonElement value,
        JsonElement schema,
        string path,
        string outputLabel,
        string schemaLabel,
        List<string> violations,
        ref bool violationsTruncated)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            AddViolation(violations, ref violationsTruncated, Invalid(path, ArraySchemaType, outputLabel));
            return;
        }

        if (!TryGetObject(schema, "items", out var itemSchema))
        {
            return;
        }

        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            ValidateElement(
                item,
                itemSchema,
                path + "[" + index + "]",
                outputLabel,
                schemaLabel,
                violations,
                ref violationsTruncated);
            if (violationsTruncated)
            {
                return;
            }

            index++;
        }
    }

    private static void ValidateString(
        JsonElement value,
        JsonElement schema,
        string path,
        string outputLabel,
        List<string> violations,
        ref bool violationsTruncated)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            AddViolation(violations, ref violationsTruncated, Invalid(path, StringSchemaType, outputLabel));
            return;
        }

        if (!schema.TryGetProperty("enum", out var enumElement) || enumElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var actual = value.GetString();
        foreach (var allowed in enumElement.EnumerateArray())
        {
            if (allowed.ValueKind == JsonValueKind.String &&
                string.Equals(actual, allowed.GetString(), StringComparison.Ordinal))
            {
                return;
            }
        }

        AddViolation(
            violations,
            ref violationsTruncated,
            $"{outputLabel} value at {path} is not allowed by its output schema.");
    }

    private static void ValidateRequiredProperties(
        JsonElement value,
        JsonElement schema,
        string path,
        string outputLabel,
        List<string> violations,
        ref bool violationsTruncated)
    {
        if (!schema.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in required.EnumerateArray())
        {
            if (violationsTruncated)
            {
                return;
            }

            if (item.ValueKind == JsonValueKind.String && !value.TryGetProperty(item.GetString()!, out _))
            {
                AddViolation(
                    violations,
                    ref violationsTruncated,
                    $"{outputLabel} is missing required property {path}.{item.GetString()}.");
            }
        }
    }

    private static string ReadSchemaType(JsonElement schema, string path, string schemaLabel)
    {
        if (!schema.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(typeElement.GetString()))
        {
            throw new InvalidOperationException($"{schemaLabel} is missing string type at {path}.");
        }

        return typeElement.GetString()!;
    }

    private static bool TryGetObject(JsonElement root, string propertyName, out JsonElement value)
    {
        if (root.TryGetProperty(propertyName, out value) && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool IsAdditionalPropertiesFalse(JsonElement schema)
    {
        return schema.TryGetProperty("additionalProperties", out var value) &&
            value.ValueKind == JsonValueKind.False;
    }

    private static void AddViolation(
        List<string> violations,
        ref bool violationsTruncated,
        string violation)
    {
        if (violations.Count < MaxReportedViolations)
        {
            violations.Add(violation);
            return;
        }

        violationsTruncated = true;
    }

    private static string Invalid(string path, string expected, string outputLabel) =>
        $"{outputLabel} at {path} must be {expected}.";

    private static WorkerOutputValidationException InvalidJson(string roleName) =>
        new(
            [$"{WorkerOutputDiagnosticLabels.Output(roleName)} is not valid JSON or one supported outer JSON fence."],
            violationsTruncated: false);
}
