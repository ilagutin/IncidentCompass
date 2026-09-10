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
/// The bounded orchestrator turn limit is a configured budget knob, so an out-of-range value must be
/// rejected when the configuration loads rather than reaching the Worker loop.
/// </summary>
public sealed class OrchestratorTurnLimitLoadValidationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(OrchestratorBudgetSettings.MaximumMaxTurns + 1)]
    public void Materialize_MaxTurnsOutOfRange_FailsLoadValidation(int maxTurns)
    {
        var node = ValidConfigNode();
        Budget(node)["MaxTurns"] = maxTurns;

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()));

        Assert.Contains("Orchestrator.Budget.MaxTurns", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OrchestratorBudgetSettings.MinimumMaxTurns)]
    [InlineData(OrchestratorBudgetSettings.DefaultMaxTurns)]
    [InlineData(OrchestratorBudgetSettings.MaximumMaxTurns)]
    public void Materialize_MaxTurnsInRange_IsAccepted(int maxTurns)
    {
        var node = ValidConfigNode();
        Budget(node)["MaxTurns"] = maxTurns;

        var configuration = CreateMaterializer().Materialize("hash-1", node, ResolvedReferences());

        Assert.Equal(maxTurns, configuration.Orchestrator.Budget.MaxTurns);
    }

    [Fact]
    public void Materialize_MaxTurnsOmitted_KeepsTheDefaultWorkTurnAllowance()
    {
        var configuration = CreateMaterializer().Materialize("hash-1", ValidConfigNode(), ResolvedReferences());

        Assert.Equal(OrchestratorBudgetSettings.DefaultMaxTurns, configuration.Orchestrator.Budget.MaxTurns);
    }

    [Theory]
    [InlineData(OrchestratorBudgetSettings.MinimumMaxTurns, true)]
    [InlineData(OrchestratorBudgetSettings.DefaultMaxTurns, true)]
    [InlineData(OrchestratorBudgetSettings.MaximumMaxTurns, true)]
    [InlineData(0, false)]
    [InlineData(OrchestratorBudgetSettings.MaximumMaxTurns + 1, false)]
    public void PublishedSchema_AcceptsTheSameMaxTurnsRangeAsLoadValidation(int maxTurns, bool expectedValid)
    {
        var configuration = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(
            RepositoryRootLocator.Find(),
            "config",
            "incidentcompass.config.json")))!;
        Budget(configuration)["MaxTurns"] = maxTurns;

        Assert.Equal(expectedValid, TriageConfigSchemaTests.EvaluateFixture(configuration).IsValid);
    }

    private static JsonObject Budget(JsonObject node) =>
        (JsonObject)((JsonObject)node["Orchestrator"]!)["Budget"]!;

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
        return new TriageConfigurationMaterializer(new TriageConfigurationLoadValidator(registry, tools, new EnvironmentModelProviderSecretReader()));
    }

    private static JsonObject ResolvedReferences() => new()
    {
        ["ref:instructions/orchestrator.md"] = "orchestrator body",
        ["ref:instructions/analysis.md"] = "analysis body",
        ["ref:schemas/analysis.json"] = "{ \"type\": \"object\" }"
    };

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
