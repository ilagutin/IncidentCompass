using System.Text.Json;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Intake.Redaction;
using static IncidentCompass.Infrastructure.Intake.TriageConfigurationValidationGuards;

namespace IncidentCompass.Infrastructure.Intake;

/// <summary>
/// Refuses a role output schema that declares a secret-named property with any type other than
/// exactly <c>string</c>.
/// <para>
/// A worker's output is validated against the role schema on the raw text, then redacted before it is
/// stored, and the delegate result is parsed from the stored form. Redaction replaces the value of a
/// secret-named property with the string <c>[REDACTED]</c> whatever its kind, so a schema that types
/// such a property as anything else describes output the stored document can never match. Refusing it
/// here fails at load, in front of the operator, instead of on every attempt of every job.
/// </para>
/// <para>
/// "Secret-named" is not restated: it is <see cref="SecretRedactor.IsSensitiveProperty"/>, the rule
/// redaction itself applies, with the configured <see cref="RedactionSettings.AttributeKeys"/>. A
/// missing <c>type</c> counts as not string, and so does a type array, even <c>["string"]</c>.
/// </para>
/// <para>
/// Walked: <c>properties</c> (each name checked, each value walked), <c>items</c> as one schema or as
/// an array of schemas, <c>additionalProperties</c> when it is a schema object, and the members of
/// <c>$defs</c> and <c>definitions</c> wherever the walk meets them. Not followed: <c>$ref</c>,
/// composition keywords (<c>allOf</c>, <c>anyOf</c>, <c>oneOf</c>, <c>not</c>), conditionals and
/// <c>patternProperties</c>. The runtime schema validator honours none of those either, and a
/// mismatch the walk misses still fails the attempt as non-retryable worker output at the delegate
/// parse. The instance path used for a path-shaped attribute key is known only through
/// <c>properties</c> and <c>items</c>; below <c>additionalProperties</c> or a definition only the
/// property name itself is compared, because the key or the reference site is not known at load.
/// </para>
/// </summary>
internal static class RoleOutputSchemaSecretPropertyLoadValidator
{
    private const string StringType = "string";
    private const string UnknownPathSegment = "*";

    public static void Validate(string roleName, JsonElement schema, RedactionSettings redaction)
    {
        Walk(roleName, schema, "#", string.Empty, redaction);
    }

    private static void Walk(
        string roleName,
        JsonElement schema,
        string schemaPath,
        string instancePath,
        RedactionSettings redaction)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                var propertySchemaPath = schemaPath + "/properties/" + EscapePointerToken(property.Name);
                var propertyInstancePath = JoinInstancePath(instancePath, property.Name);
                RequireStringTypeWhenSensitive(roleName, property, propertySchemaPath, propertyInstancePath, redaction);
                Walk(roleName, property.Value, propertySchemaPath, propertyInstancePath, redaction);
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            WalkItems(roleName, items, schemaPath + "/items", instancePath, redaction);
        }

        if (schema.TryGetProperty("additionalProperties", out var additional))
        {
            Walk(roleName, additional, schemaPath + "/additionalProperties", JoinInstancePath(instancePath, UnknownPathSegment), redaction);
        }

        WalkDefinitions(roleName, schema, "$defs", schemaPath, redaction);
        WalkDefinitions(roleName, schema, "definitions", schemaPath, redaction);
    }

    private static void WalkItems(
        string roleName,
        JsonElement items,
        string schemaPath,
        string instancePath,
        RedactionSettings redaction)
    {
        if (items.ValueKind != JsonValueKind.Array)
        {
            // An array element adds no segment to the path redaction compares attribute keys against.
            Walk(roleName, items, schemaPath, instancePath, redaction);
            return;
        }

        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            Walk(roleName, item, schemaPath + "/" + index, instancePath, redaction);
            index++;
        }
    }

    private static void WalkDefinitions(
        string roleName,
        JsonElement schema,
        string keyword,
        string schemaPath,
        RedactionSettings redaction)
    {
        if (!schema.TryGetProperty(keyword, out var definitions) || definitions.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var definition in definitions.EnumerateObject())
        {
            Walk(
                roleName,
                definition.Value,
                schemaPath + "/" + keyword + "/" + EscapePointerToken(definition.Name),
                UnknownPathSegment,
                redaction);
        }
    }

    private static void RequireStringTypeWhenSensitive(
        string roleName,
        JsonProperty property,
        string schemaPath,
        string instancePath,
        RedactionSettings redaction)
    {
        if (!SecretRedactor.IsSensitiveProperty(property.Name, instancePath, redaction) ||
            DeclaresExactlyStringType(property.Value))
        {
            return;
        }

        var rule = SecretPropertyNameMatcher.IsSensitive(property.Name)
            ? "the built-in secret property-name denylist"
            : "Redaction.AttributeKeys";
        throw Invalid(
            "Roles." + roleName + ".OutputSchema",
            schemaPath,
            "type \"string\" for a property that " + rule +
                " redacts, because redaction replaces its value with the string [REDACTED] whatever its declared type");
    }

    private static bool DeclaresExactlyStringType(JsonElement propertySchema) =>
        propertySchema.ValueKind == JsonValueKind.Object &&
        propertySchema.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String &&
        string.Equals(type.GetString(), StringType, StringComparison.Ordinal);

    private static string JoinInstancePath(string instancePath, string segment) =>
        string.IsNullOrEmpty(instancePath) ? segment : instancePath + "." + segment;

    private static string EscapePointerToken(string token) =>
        token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
