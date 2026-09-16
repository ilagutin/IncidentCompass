using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Intake.Normalization;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.Intake;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Redaction replaces the value of a secret-named property with the string <c>[REDACTED]</c> whatever
/// its kind, after the worker output was validated against the role schema. A role schema that types
/// such a property as anything but a string therefore describes output the stored document can never
/// match, so the configuration load refuses it, naming the role, the schema path and the rule.
/// </summary>
public sealed class RoleOutputSchemaSecretPropertyLoadValidationTests
{
    [Theory]
    [InlineData("""{ "type": "object" }""")]
    [InlineData("""{ "type": "number" }""")]
    [InlineData("""{ "description": "no type" }""")]
    [InlineData("""{ "type": ["string", "null"] }""")]
    public void Materialize_SecretNamedPropertyWithNonStringType_FailsLoadValidation(string propertySchema)
    {
        var schema = """{ "type": "object", "properties": { "apiToken": """ + propertySchema + " } }";

        var exception = Assert.Throws<TriageConfigurationLoadException>(() => Materialize(schema));

        Assert.Contains("Roles.analysis.OutputSchema", exception.Message, StringComparison.Ordinal);
        Assert.Contains("#/properties/apiToken", exception.Message, StringComparison.Ordinal);
        Assert.Contains("built-in secret property-name denylist", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_SecretNamedPropertyTypedString_Loads()
    {
        var schema = """{ "type": "object", "properties": { "apiToken": { "type": "string" }, "tokenCount": { "type": "string" } } }""";

        var configuration = Materialize(schema);

        Assert.Equal(schema, configuration.Roles["analysis"].OutputSchema);
    }

    [Fact]
    public void Materialize_PropertySensitiveOnlyThroughConfiguredAttributeKey_FailsLoadValidation()
    {
        var schema = """{ "type": "object", "properties": { "retryCount": { "type": "number" } } }""";

        Assert.Equal(schema, Materialize(schema).Roles["analysis"].OutputSchema);
        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            Materialize(schema, attributeKeys: ["RETRYCOUNT"]));

        Assert.Contains("Roles.analysis.OutputSchema", exception.Message, StringComparison.Ordinal);
        Assert.Contains("#/properties/retryCount", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Redaction.AttributeKeys", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_ConfiguredAttributeKeyMatchedByDottedPathThroughItems_FailsLoadValidation()
    {
        // Redaction compares a dotted path from the document root and array elements add no segment,
        // so "evidence.weight" names the property inside each element of the evidence array.
        var schema = """
            { "type": "object", "properties": { "evidence": { "type": "array", "items": {
              "type": "object", "properties": { "weight": { "type": "number" } } } } } }
            """;

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            Materialize(schema, attributeKeys: ["evidence.weight"]));

        Assert.Contains("#/properties/evidence/items/properties/weight", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        """{ "type": "object", "properties": { "outer": { "type": "object", "properties": { "inner": { "type": "object", "properties": { "password": { "type": "boolean" } } } } } } }""",
        "#/properties/outer/properties/inner/properties/password")]
    [InlineData(
        """{ "type": "object", "properties": { "items": { "type": "array", "items": { "type": "object", "properties": { "session": { "type": "object" } } } } } }""",
        "#/properties/items/items/properties/session")]
    [InlineData(
        """{ "type": "object", "properties": { "rows": { "type": "array", "items": [ { "type": "string" }, { "type": "object", "properties": { "x-api-key": { "type": "number" } } } ] } } }""",
        "#/properties/rows/items/1/properties/x-api-key")]
    [InlineData(
        """{ "type": "object", "additionalProperties": { "type": "object", "properties": { "clientSecret": { "type": "array" } } } }""",
        "#/additionalProperties/properties/clientSecret")]
    [InlineData(
        """{ "type": "object", "$defs": { "credential": { "type": "object", "properties": { "privateKey": { "type": "object" } } } } }""",
        "#/$defs/credential/properties/privateKey")]
    [InlineData(
        """{ "type": "object", "properties": { "a": { "type": "object", "definitions": { "d": { "type": "object", "properties": { "jwt": {} } } } } } }""",
        "#/properties/a/definitions/d/properties/jwt")]
    public void Materialize_NestedSecretNamedProperty_IsWalkedAndNamed(string schema, string expectedPath)
    {
        var exception = Assert.Throws<TriageConfigurationLoadException>(() => Materialize(schema));

        Assert.Contains("Roles.analysis.OutputSchema", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expectedPath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_SecretNamedPropertyReachedOnlyThroughRef_IsNotFollowed()
    {
        // The walk does not resolve $ref. The property that holds the reference is itself named
        // harmlessly, so this loads; the delegate parse is the documented second line for it.
        var schema = """{ "type": "object", "properties": { "auth": { "$ref": "#/components/credential" } }, "components": { "credential": { "type": "object", "properties": { "token": { "type": "number" } } } } }""";

        Assert.Equal(schema, Materialize(schema).Roles["analysis"].OutputSchema);
    }

    public static TheoryData<string> TriageConfigurationPaths
    {
        get
        {
            var configurationPaths = TriageConfigurationFileLocator.FindAll();
            TriageConfigurationFileLocator.AssertDiscoveryCoversEveryKnownConfiguration(configurationPaths);
            var data = new TheoryData<string>();
            foreach (var configurationPath in configurationPaths)
            {
                data.Add(configurationPath);
            }

            return data;
        }
    }

    /// <summary>
    /// Every role schema the repository carries, shipped and fixture, passes the same walk with its
    /// own configured attribute keys. The full load of these files is covered elsewhere; this reads
    /// only the roles and the redaction keys so it does not depend on host secrets.
    /// </summary>
    [Theory]
    [MemberData(nameof(TriageConfigurationPaths))]
    public void EveryRepositoryConfiguration_RoleSchemasPassTheSecretPropertyWalk(string configurationPath)
    {
        var configuration = JsonNode.Parse(File.ReadAllText(configurationPath))!.AsObject();
        var attributeKeys = configuration["Redaction"]?["AttributeKeys"]?.AsArray()
            .Select(key => key!.GetValue<string>())
            .ToArray() ?? [];
        var redaction = new RedactionSettings(attributeKeys, [], []);
        var roles = configuration["Roles"]!.AsObject();
        Assert.NotEmpty(roles);

        foreach (var (roleName, role) in roles)
        {
            var outputSchema = role!["OutputSchema"]!.GetValue<string>();
            var schemaText = outputSchema.StartsWith("ref:", StringComparison.Ordinal)
                ? File.ReadAllText(Path.Combine(Path.GetDirectoryName(configurationPath)!, outputSchema["ref:".Length..]))
                : outputSchema;
            using var document = JsonDocument.Parse(schemaText);

            RoleOutputSchemaSecretPropertyLoadValidator.Validate(roleName, document.RootElement, redaction);
        }
    }

    private static TriageConfiguration Materialize(string schema, string[]? attributeKeys = null)
    {
        var node = ValidConfigNode();
        node["Redaction"] = new JsonObject
        {
            ["AttributeKeys"] = new JsonArray((attributeKeys ?? []).Select(key => (JsonNode)key).ToArray()),
            ["Patterns"] = new JsonArray(),
            ["UserIdentifierAttributes"] = new JsonArray()
        };
        var references = new JsonObject
        {
            ["ref:instructions/orchestrator.md"] = "orchestrator body",
            ["ref:instructions/analysis.md"] = "analysis body",
            ["ref:schemas/analysis.json"] = schema
        };

        return CreateMaterializer().Materialize("hash-1", node, references);
    }

    private static TriageConfigurationMaterializer CreateMaterializer()
    {
        var registry = new SignalNormalizerRegistry([
            new TesterSignalNormalizer(),
            new OtelShapedSignalNormalizer(),
            new UserReportSignalNormalizer()
        ]);
        var tools = new AgentToolRegistry([
            new AgentToolDescriptor("memory_search", AgentToolCapability.ImmediateRead),
            TicketCreateTool.Descriptor
        ]);
        return new TriageConfigurationMaterializer(
            new TriageConfigurationLoadValidator(registry, tools, new EnvironmentModelProviderSecretReader()));
    }

    private static JsonObject ValidConfigNode()
    {
        return (JsonObject)JsonNode.Parse("""
            {
              "Providers": {
                "local-oai": { "Kind": "OpenAICompatible", "Endpoint": "http://localhost:1234/v1", "ApiKeySecretRef": "LOCAL_OAI_KEY" }
              },
              "Routes": {
                "analysis-chat": { "Kind": "Chat", "ProviderId": "local-oai", "Model": "local-model", "Temperature": 0.1, "MaxOutputTokens": 2000, "ContextWindowTokens": 8192 },
                "report-chat": { "Kind": "Chat", "ProviderId": "local-oai", "Model": "local-model", "Temperature": 0.2, "MaxOutputTokens": 4000, "ContextWindowTokens": 8192 },
                "memory-embed": { "Kind": "Embedding", "ProviderId": "local-oai", "Model": "mock-memory-embedding-v1" }
              },
              "Orchestrator": {
                "Instructions": "ref:instructions/orchestrator.md",
                "RouteId": "report-chat",
                "Tools": ["delegate", "publish_report"],
                "Budget": { "MaxWorkers": 6, "MaxTokens": 200000, "MaxWallClockSeconds": 120 }
              },
              "Roles": {
                "analysis": { "RouteId": "analysis-chat", "Instructions": "ref:instructions/analysis.md", "Tools": [], "OutputSchema": "ref:schemas/analysis.json" }
              },
              "Tools": {
                "memory_search": { "Kind": "internal", "EmbeddingRouteId": "memory-embed", "TopK": 5, "MinScore": 0.25 }
              },
              "Rules": [
                { "Type": "rate_cap", "Tool": "*", "Max": 50 }
              ],
              "Ingestion": { "DefaultTenant": "local", "AllowedSources": ["otel", "user", "tester", "manual"] },
              "FaultGrouping": {
                "LookbackMinutes": 15,
                "SilenceWindowMinutes": 30,
                "FingerprintVersion": 1,
                "MassIssue": { "MinNeighborCount": 5, "MinFingerprintStrength": "strong" }
              }
            }
            """)!;
    }
}
