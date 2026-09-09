using System.Text.Json;
using System.Text.RegularExpressions;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

public sealed class ShippedWorkerInstructionExamplesTests
{
    private static readonly HashSet<string> SupportedSchemaTypes =
        new(["object", "array", "string", "boolean", "number"], StringComparer.Ordinal);

    private static readonly Regex JsonExamplePattern = new(
        "~~~json\\r?\\n(?<example>\\{[\\s\\S]*?\\})\\r?\\n~~~",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Counts JSON example blocks independently of <see cref="JsonExamplePattern"/> so that an example
    /// the capture pattern cannot read (a ``` fence, or a body that does not start with '{') fails the
    /// test loudly instead of being skipped.
    /// </summary>
    private static readonly Regex JsonExampleFenceOpeningPattern = new(
        "^(?:~~~|```)json[ \\t]*\\r?$",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);

    [Fact]
    public void EveryShippedRoleInstructionJsonExample_ValidatesItsRoleOutputSchema()
    {
        var root = RepositoryRootLocator.Find();
        var configPath = Path.Combine(root, "config", "incidentcompass.config.json");
        using var configDocument = JsonDocument.Parse(File.ReadAllText(configPath));
        var roles = configDocument.RootElement.GetProperty("Roles");

        foreach (var role in roles.EnumerateObject())
        {
            var instructions = File.ReadAllText(ResolveConfigReference(root, role.Value.GetProperty("Instructions").GetString()!));
            var outputSchema = File.ReadAllText(ResolveConfigReference(root, role.Value.GetProperty("OutputSchema").GetString()!));
            var examples = JsonExamplePattern.Matches(instructions);
            var fenceOpenings = JsonExampleFenceOpeningPattern.Matches(instructions);

            Assert.NotEmpty(examples);
            Assert.True(
                examples.Count == fenceOpenings.Count,
                $"Role '{role.Name}' instructions open {fenceOpenings.Count} JSON example blocks but only " +
                $"{examples.Count} were captured for validation. Every JSON example must be validated, so " +
                "write the example as a ~~~json block whose body is a single JSON object.");
            foreach (Match example in examples)
            {
                AnalysisWorkerOutputSchemaValidator.Validate(
                    example.Groups["example"].Value,
                    outputSchema,
                    role.Name);
            }
        }
    }

    [Fact]
    public void EveryShippedRoleSchema_UsesSupportedStringTypesWithoutUnions()
    {
        var root = RepositoryRootLocator.Find();
        var configPath = Path.Combine(root, "config", "incidentcompass.config.json");
        using var configDocument = JsonDocument.Parse(File.ReadAllText(configPath));

        foreach (var role in configDocument.RootElement.GetProperty("Roles").EnumerateObject())
        {
            var schemaPath = ResolveConfigReference(root, role.Value.GetProperty("OutputSchema").GetString()!);
            using var schemaDocument = JsonDocument.Parse(File.ReadAllText(schemaPath));

            AssertSupportedSchemaNode(schemaDocument.RootElement, role.Name, "output");
        }
    }

    private static void AssertSupportedSchemaNode(JsonElement schema, string roleName, string path)
    {
        Assert.True(
            schema.TryGetProperty("type", out var type),
            $"Role '{roleName}' schema is missing type at {path}.");
        Assert.True(
            type.ValueKind == JsonValueKind.String,
            $"Role '{roleName}' schema type at {path} must be one supported string, not an array or union.");
        Assert.Contains(type.GetString()!, SupportedSchemaTypes);

        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                AssertSupportedSchemaNode(property.Value, roleName, path + "." + property.Name);
            }
        }

        if (schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            AssertSupportedSchemaNode(items, roleName, path + "[]");
        }
    }

    private static string ResolveConfigReference(string root, string reference)
    {
        const string prefix = "ref:";
        Assert.StartsWith(prefix, reference, StringComparison.Ordinal);
        return Path.Combine(root, "config", reference[prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
    }
}
