using System.Text.Json;
using System.Text.RegularExpressions;
using IncidentCompass.Application.Investigation.Jobs;

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
        var configPath = TriageConfigurationFileLocator.Shipped();
        using var configDocument = JsonDocument.Parse(File.ReadAllText(configPath));
        var roles = configDocument.RootElement.GetProperty("Roles");

        foreach (var role in roles.EnumerateObject())
        {
            var instructions = File.ReadAllText(TriageConfigurationFileLocator.ResolveReference(
                configPath, role.Value.GetProperty("Instructions").GetString()!));
            var outputSchema = File.ReadAllText(TriageConfigurationFileLocator.ResolveReference(
                configPath, role.Value.GetProperty("OutputSchema").GetString()!));
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

    /// <summary>
    /// Every configuration in the repository, not only the shipped one: <c>WorkerRoleRunner</c>
    /// validates a role's output against whichever configuration the job was snapshotted from, using
    /// the same validator. A test fixture whose role schema uses a construct the validator cannot
    /// evaluate throws <see cref="InvalidOperationException" /> from inside the worker loop, which is
    /// not the <c>WorkerOutputValidationException</c> the reprompt path catches, so the attempt dies
    /// instead of correcting itself. This assertion is what keeps the fixture and shipped schema
    /// dialects from diverging again.
    /// </summary>
    [Fact]
    public void EveryConfiguredRoleSchema_UsesSupportedStringTypesWithoutUnions()
    {
        var configPaths = TriageConfigurationFileLocator.FindAll();

        TriageConfigurationFileLocator.AssertDiscoveryCoversShippedAndFixtureConfigurations(configPaths);
        foreach (var configPath in configPaths)
        {
            using var configDocument = JsonDocument.Parse(File.ReadAllText(configPath));
            foreach (var role in configDocument.RootElement.GetProperty("Roles").EnumerateObject())
            {
                var schemaPath = TriageConfigurationFileLocator.ResolveReference(
                    configPath, role.Value.GetProperty("OutputSchema").GetString()!);
                using var schemaDocument = JsonDocument.Parse(File.ReadAllText(schemaPath));

                AssertSupportedSchemaNode(schemaDocument.RootElement, configPath, role.Name, "output");
            }
        }
    }

    private static void AssertSupportedSchemaNode(
        JsonElement schema,
        string configPath,
        string roleName,
        string path)
    {
        Assert.True(
            schema.TryGetProperty("type", out var type),
            $"Role '{roleName}' in '{configPath}' is missing type at {path}.");
        Assert.True(
            type.ValueKind == JsonValueKind.String,
            $"Role '{roleName}' in '{configPath}' declares a type at {path} that must be one supported " +
            "string, not an array or union.");
        Assert.Contains(type.GetString()!, SupportedSchemaTypes);

        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                AssertSupportedSchemaNode(property.Value, configPath, roleName, path + "." + property.Name);
            }
        }

        if (schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            AssertSupportedSchemaNode(items, configPath, roleName, path + "[]");
        }
    }
}
