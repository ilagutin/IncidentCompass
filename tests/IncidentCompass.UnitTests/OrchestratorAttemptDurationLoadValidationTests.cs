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
/// The attempt duration ceiling moved from the required <c>MaxWallClockSeconds</c> to the optional
/// <c>MaxAttemptDurationSeconds</c>. An existing configuration and every stored snapshot with the old
/// key must keep loading with its explicit value, a configuration with neither key gets the long
/// default, and the loader and the published schema must agree on what is accepted.
/// </summary>
public sealed class OrchestratorAttemptDurationLoadValidationTests
{
    private const string Current = "MaxAttemptDurationSeconds";
    private const string Deprecated = "MaxWallClockSeconds";

    [Fact]
    public void Materialize_OldKeyOnly_UsesItsExplicitValueAsTheCeiling()
    {
        var node = ConfigNode((Deprecated, 600));

        var budget = CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()).Orchestrator.Budget;

        Assert.True(budget.UsesDeprecatedMaxWallClockSeconds());
        Assert.Equal(600, budget.ResolveAttemptDurationSeconds());
        Assert.Equal(TimeSpan.FromSeconds(600), budget.ResolveAttemptDurationLimit());
        Assert.Equal(OrchestratorBudgetSettings.MaxWallClockSecondsSettingName, budget.ResolveAttemptDurationSettingName());
    }

    [Fact]
    public void Materialize_NewKeyOnly_UsesItsValue()
    {
        var node = ConfigNode((Current, 7200));

        var budget = CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()).Orchestrator.Budget;

        Assert.False(budget.UsesDeprecatedMaxWallClockSeconds());
        Assert.Equal(TimeSpan.FromSeconds(7200), budget.ResolveAttemptDurationLimit());
        Assert.Equal(OrchestratorBudgetSettings.MaxAttemptDurationSecondsSettingName, budget.ResolveAttemptDurationSettingName());
    }

    [Fact]
    public void Materialize_NeitherKey_DefaultsToFourHours()
    {
        var node = ConfigNode();

        var budget = CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()).Orchestrator.Budget;

        Assert.Equal(14_400, budget.ResolveAttemptDurationSeconds());
        Assert.Equal(TimeSpan.FromHours(4), budget.ResolveAttemptDurationLimit());
    }

    [Fact]
    public void Materialize_ZeroOnTheNewKey_DisablesTheCeiling()
    {
        var node = ConfigNode((Current, 0));

        var budget = CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()).Orchestrator.Budget;

        Assert.Equal(0, budget.ResolveAttemptDurationSeconds());
        Assert.Null(budget.ResolveAttemptDurationLimit());
    }

    [Fact]
    public void Materialize_BothKeys_FailsNamingBoth()
    {
        var node = ConfigNode((Current, 7200), (Deprecated, 600));

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()));

        Assert.Contains("Orchestrator.Budget.MaxAttemptDurationSeconds", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Orchestrator.Budget.MaxWallClockSeconds", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Current, -1)]
    [InlineData(Current, OrchestratorBudgetSettings.MaximumMaxAttemptDurationSeconds + 1)]
    [InlineData(Deprecated, 0)]
    [InlineData(Deprecated, -1)]
    public void Materialize_OutOfRange_FailsNamingTheKey(string key, int seconds)
    {
        var node = ConfigNode((key, seconds));

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()));

        Assert.Contains("Orchestrator.Budget." + key, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Current, 1)]
    [InlineData(Current, OrchestratorBudgetSettings.MaximumMaxAttemptDurationSeconds)]
    [InlineData(Deprecated, 1)]
    public void Materialize_BoundaryValues_AreAccepted(string key, int seconds)
    {
        var node = ConfigNode((key, seconds));

        var budget = CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()).Orchestrator.Budget;

        Assert.Equal(seconds, budget.ResolveAttemptDurationSeconds());
    }

    public static TheoryData<string?, int, string?, int, bool> SchemaCases => new()
    {
        { null, 0, null, 0, true },
        { Current, 14_400, null, 0, true },
        { Current, 0, null, 0, true },
        { Current, OrchestratorBudgetSettings.MaximumMaxAttemptDurationSeconds, null, 0, true },
        { Deprecated, 600, null, 0, true },
        { Current, -1, null, 0, false },
        { Current, OrchestratorBudgetSettings.MaximumMaxAttemptDurationSeconds + 1, null, 0, false },
        { Deprecated, 0, null, 0, false },
        { Current, 7200, Deprecated, 600, false }
    };

    [Theory]
    [MemberData(nameof(SchemaCases))]
    public void PublishedSchema_AcceptsTheSameShapesAsLoadValidation(
        string? firstKey,
        int firstSeconds,
        string? secondKey,
        int secondSeconds,
        bool expectedValid)
    {
        var configuration = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(
            RepositoryRootLocator.Find(),
            "config",
            "incidentcompass.config.json")))!;
        var budget = Budget(configuration);
        budget.Remove(Current);
        budget.Remove(Deprecated);
        if (firstKey is not null)
        {
            budget[firstKey] = firstSeconds;
        }

        if (secondKey is not null)
        {
            budget[secondKey] = secondSeconds;
        }

        Assert.Equal(expectedValid, TriageConfigSchemaTests.EvaluateFixture(configuration).IsValid);
    }

    internal static JsonObject ConfigNode(params (string Key, int Seconds)[] attemptDuration)
    {
        var node = (JsonObject)JsonNode.Parse("""
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
                "Budget": { "MaxWorkers": 6, "MaxTokens": 200000 }
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
        foreach (var (key, seconds) in attemptDuration)
        {
            Budget(node)[key] = seconds;
        }

        return node;
    }

    internal static JsonObject ResolvedReferences() => new()
    {
        ["ref:instructions/orchestrator.md"] = "orchestrator body",
        ["ref:instructions/analysis.md"] = "analysis body",
        ["ref:schemas/analysis.json"] = "{ \"type\": \"object\" }"
    };

    internal static TriageConfigurationMaterializer CreateMaterializer()
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

    private static JsonObject Budget(JsonObject node) =>
        (JsonObject)((JsonObject)node["Orchestrator"]!)["Budget"]!;
}
