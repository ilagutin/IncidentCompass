using System.Text.Json.Nodes;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Intake.Normalization;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.Intake;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The repetition, progress and recovery limits are optional budget knobs: absent keys keep the defaults and the
/// configuration hash, and an out-of-range value is refused at load and by the published schema alike.
/// </summary>
public sealed class OrchestratorProgressBudgetLoadValidationTests
{
    [Theory]
    [InlineData("MaxEquivalentCalls", OrchestratorBudgetSettings.MinimumMaxEquivalentCalls - 1)]
    [InlineData("MaxEquivalentCalls", OrchestratorBudgetSettings.MaximumMaxEquivalentCalls + 1)]
    [InlineData("MaxTurnsWithoutProgress", OrchestratorBudgetSettings.MinimumMaxTurnsWithoutProgress - 1)]
    [InlineData("MaxTurnsWithoutProgress", OrchestratorBudgetSettings.MaximumMaxTurnsWithoutProgress + 1)]
    [InlineData("MaxRecoveries", OrchestratorBudgetSettings.MinimumMaxRecoveries - 1)]
    [InlineData("MaxRecoveries", OrchestratorBudgetSettings.MaximumMaxRecoveries + 1)]
    public void Materialize_OutOfRange_FailsLoadValidationNamingTheSetting(string key, int value)
    {
        var node = ValidConfigNode();
        Budget(node)[key] = value;

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()));

        Assert.Contains("Orchestrator.Budget." + key, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OrchestratorBudgetSettings.MinimumMaxEquivalentCalls, OrchestratorBudgetSettings.MinimumMaxTurnsWithoutProgress, OrchestratorBudgetSettings.MinimumMaxRecoveries)]
    [InlineData(OrchestratorBudgetSettings.MaximumMaxEquivalentCalls, OrchestratorBudgetSettings.MaximumMaxTurnsWithoutProgress, OrchestratorBudgetSettings.MaximumMaxRecoveries)]
    public void Materialize_InRange_IsAccepted(int maxEquivalentCalls, int maxTurnsWithoutProgress, int maxRecoveries)
    {
        var node = ValidConfigNode();
        Budget(node)["MaxEquivalentCalls"] = maxEquivalentCalls;
        Budget(node)["MaxTurnsWithoutProgress"] = maxTurnsWithoutProgress;
        Budget(node)["MaxRecoveries"] = maxRecoveries;

        var budget = CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()).Orchestrator.Budget;

        Assert.Equal(maxEquivalentCalls, budget.MaxEquivalentCalls);
        Assert.Equal(maxTurnsWithoutProgress, budget.MaxTurnsWithoutProgress);
        Assert.Equal(maxRecoveries, budget.MaxRecoveries);
    }

    [Fact]
    public void Materialize_RecoveryInstructionsReference_IsResolvedLikeInstructions()
    {
        var node = ValidConfigNode();
        ((JsonObject)node["Orchestrator"]!)["RecoveryInstructions"] = "ref:instructions/recovery.md";
        var references = ResolvedReferences();
        references["ref:instructions/recovery.md"] = "recovery body";

        var orchestrator = CreateMaterializer().Materialize("hash-1", node, references).Orchestrator;

        Assert.Equal("recovery body", orchestrator.RecoveryInstructions);
        Assert.Equal("recovery body", InvestigationRecoveryInstructions.Resolve(orchestrator));
    }

    [Fact]
    public void Materialize_BlankRecoveryInstructions_FailsLoadValidation()
    {
        var node = ValidConfigNode();
        ((JsonObject)node["Orchestrator"]!)["RecoveryInstructions"] = " ";

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()));

        Assert.Contains("Orchestrator.RecoveryInstructions", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShippedRecoveryInstructionsFile_IsTheBuiltInDefault()
    {
        var shipped = File.ReadAllText(Path.Combine(RepositoryRootLocator.Find(), "config", "instructions", "recovery.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal(InvestigationRecoveryInstructions.Default, shipped);
    }

    [Fact]
    public void Materialize_KeysOmitted_KeepTheDefaultsAndTheHashInput()
    {
        var node = ValidConfigNode();
        var before = node.ToJsonString();

        var budget = CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()).Orchestrator.Budget;

        Assert.Equal(OrchestratorBudgetSettings.DefaultMaxEquivalentCalls, budget.MaxEquivalentCalls);
        Assert.Equal(OrchestratorBudgetSettings.DefaultMaxTurnsWithoutProgress, budget.MaxTurnsWithoutProgress);
        Assert.Equal(OrchestratorBudgetSettings.DefaultMaxRecoveries, budget.MaxRecoveries);

        // The configuration hash is taken over the file as written, so a key nobody sets cannot move
        // it; materializing must not write the defaults back into the document either.
        Assert.Equal(before, node.ToJsonString());
        Assert.False(Budget(node).ContainsKey("MaxEquivalentCalls"));
        Assert.False(Budget(node).ContainsKey("MaxTurnsWithoutProgress"));
        Assert.False(Budget(node).ContainsKey("MaxRecoveries"));
    }

    [Theory]
    [InlineData("MaxEquivalentCalls", OrchestratorBudgetSettings.MinimumMaxEquivalentCalls, true)]
    [InlineData("MaxEquivalentCalls", OrchestratorBudgetSettings.MaximumMaxEquivalentCalls, true)]
    [InlineData("MaxEquivalentCalls", OrchestratorBudgetSettings.MinimumMaxEquivalentCalls - 1, false)]
    [InlineData("MaxEquivalentCalls", OrchestratorBudgetSettings.MaximumMaxEquivalentCalls + 1, false)]
    [InlineData("MaxTurnsWithoutProgress", OrchestratorBudgetSettings.MinimumMaxTurnsWithoutProgress, true)]
    [InlineData("MaxTurnsWithoutProgress", OrchestratorBudgetSettings.MaximumMaxTurnsWithoutProgress, true)]
    [InlineData("MaxTurnsWithoutProgress", OrchestratorBudgetSettings.MinimumMaxTurnsWithoutProgress - 1, false)]
    [InlineData("MaxTurnsWithoutProgress", OrchestratorBudgetSettings.MaximumMaxTurnsWithoutProgress + 1, false)]
    [InlineData("MaxRecoveries", OrchestratorBudgetSettings.MinimumMaxRecoveries, true)]
    [InlineData("MaxRecoveries", OrchestratorBudgetSettings.MaximumMaxRecoveries, true)]
    [InlineData("MaxRecoveries", OrchestratorBudgetSettings.MinimumMaxRecoveries - 1, false)]
    [InlineData("MaxRecoveries", OrchestratorBudgetSettings.MaximumMaxRecoveries + 1, false)]
    public void PublishedSchema_AcceptsTheSameRangeAsLoadValidation(string key, int value, bool expectedValid)
    {
        var configuration = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(
            RepositoryRootLocator.Find(),
            "config",
            "incidentcompass.config.json")))!;
        Budget(configuration)[key] = value;

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
