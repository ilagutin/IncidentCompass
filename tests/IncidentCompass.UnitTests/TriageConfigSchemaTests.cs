using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.TestSupport;
using Json.Schema;

namespace IncidentCompass.UnitTests;

public sealed class TriageConfigSchemaTests
{
    private static readonly Lazy<JsonSchema> Schema = new(BuildSchema);

    /// <summary>
    /// Every triage configuration the repository carries, discovered rather than listed: the shipped
    /// one, the evaluation one and the integration test fixtures are all loaded by the same loader,
    /// so a configuration that drifts out of the published schema is a defect wherever it lives, and
    /// adding a configuration must not silently add an unchecked one.
    /// </summary>
    [Fact]
    public void EveryConfigurationInTheRepository_MatchesPublishedSchema()
    {
        var configurationPaths = TriageConfigurationFileLocator.FindAll();

        TriageConfigurationFileLocator.AssertDiscoveryCoversShippedAndFixtureConfigurations(configurationPaths);
        foreach (var configurationPath in configurationPaths)
        {
            var result = Evaluate(LoadConfiguration(configurationPath));

            Assert.True(result.IsValid, $"Configuration is schema-invalid: {configurationPath}");
        }
    }

    [Fact]
    public void MissingRequiredSection_IsRejected()
    {
        var configuration = LoadConfiguration();
        configuration.AsObject().Remove("Ingestion");

        var result = Evaluate(configuration);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void CurrentReleasesRequiresNonBlankServiceAndRelease()
    {
        var configuration = LoadConfiguration();
        configuration["CurrentReleases"] = new JsonObject
        {
            ["checkout"] = "",
            [""] = "2026.07.13.1"
        };

        var result = Evaluate(configuration);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void InvalidBudgetAndRedactionShape_AreRejected()
    {
        var configuration = LoadConfiguration();
        configuration["Orchestrator"]!["Budget"]!["MaxTokens"] = 0;
        configuration["Redaction"]!["Patterns"]![0]!["Name"] = string.Empty;

        var result = Evaluate(configuration);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void PublicTriageConfigurationHasNoTicketCredentialOrRepositorySurface()
    {
        var configurationNode = LoadConfiguration();
        var ticketSurface = configurationNode["Roles"]!["tickets"]!.ToJsonString() +
            configurationNode["Tools"]!["ticket_search"]!.ToJsonString();
        var schema = File.ReadAllText(Path.Combine(RepositoryRootLocator.Find(), "config", "incidentcompass.schema.json"));

        Assert.DoesNotContain("token", ticketSurface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("github", ticketSurface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("repository", ticketSurface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ticket token", schema, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublicNotificationConfigurationHasNoTelegramCredentialOrRecipientSurface()
    {
        var configuration = LoadConfiguration();
        var actions = configuration["Actions"]!.ToJsonString();
        var schema = File.ReadAllText(Path.Combine(RepositoryRootLocator.Find(), "config", "incidentcompass.schema.json"));

        Assert.DoesNotContain("BotToken", actions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ChatId", actions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api.telegram.org", actions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BotToken", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ChatId", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api.telegram.org", schema, StringComparison.OrdinalIgnoreCase);
    }

    internal static EvaluationResults EvaluateFixture(JsonNode configuration) => Evaluate(configuration);

    private static EvaluationResults Evaluate(JsonNode configuration)
    {
        using var configurationDocument = JsonDocument.Parse(configuration.ToJsonString());
        return Schema.Value.Evaluate(configurationDocument.RootElement);
    }

    private static JsonSchema BuildSchema()
    {
        using var schemaDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRootLocator.Find(),
            "config",
            "incidentcompass.schema.json")));
        return JsonSchema.Build(schemaDocument.RootElement.Clone());
    }

    private static JsonNode LoadConfiguration(string? path = null) =>
        JsonNode.Parse(File.ReadAllText(path ?? TriageConfigurationFileLocator.Shipped()))!;
}
