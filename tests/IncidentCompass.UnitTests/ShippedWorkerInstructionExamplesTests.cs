using System.Text.Json;
using System.Text.RegularExpressions;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

public sealed class ShippedWorkerInstructionExamplesTests
{
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

    private static string ResolveConfigReference(string root, string reference)
    {
        const string prefix = "ref:";
        Assert.StartsWith(prefix, reference, StringComparison.Ordinal);
        return Path.Combine(root, "config", reference[prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
    }
}
