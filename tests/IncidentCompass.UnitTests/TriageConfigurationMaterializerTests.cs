using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Normalization;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Infrastructure.Intake;

namespace IncidentCompass.UnitTests;

public sealed class TriageConfigurationMaterializerTests
{
    [Fact]
    public void Materialize_ResolvesReferencesAndDefaultsRuleScope()
    {
        var configuration = CreateMaterializer().Materialize(
            "hash-1",
            ValidConfigNode(),
            ResolvedReferences());

        Assert.Equal("hash-1", configuration.ConfigHash);
        Assert.Equal("orchestrator body", configuration.Orchestrator.Instructions);
        Assert.Equal("analysis body", configuration.Roles["analysis"].Instructions);
        Assert.Equal("{ \"type\": \"object\" }", configuration.Roles["analysis"].OutputSchema);
        var rule = Assert.Single(configuration.Rules);
        Assert.Equal("attempt", rule.Scope);
        Assert.Empty(configuration.Redaction.Patterns);
        Assert.Empty(configuration.CurrentReleases);
        Assert.Null(configuration.Routes["analysis-chat"].Reasoning);
    }

    [Theory]
    [InlineData("off", AiReasoningLevel.Off)]
    [InlineData("low", AiReasoningLevel.Low)]
    [InlineData("medium", AiReasoningLevel.Medium)]
    [InlineData("high", AiReasoningLevel.High)]
    public void Materialize_MapsStrictLowercaseReasoningLevel(
        string configuredValue,
        AiReasoningLevel expected)
    {
        var node = ValidConfigNode();
        node["Routes"]!["analysis-chat"]!["Reasoning"] = configuredValue;

        var configuration = CreateMaterializer().Materialize("hash-1", node, ResolvedReferences());

        Assert.Equal(expected, configuration.Routes["analysis-chat"].Reasoning);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("Low")]
    public void Materialize_RejectsUnknownOrWrongCaseReasoningString(string configuredValue)
    {
        var node = ValidConfigNode();
        node["Routes"]!["analysis-chat"]!["Reasoning"] = configuredValue;

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()));

        Assert.Contains("Routes.analysis-chat.Reasoning", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"unsupported value '{configuredValue}'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("off, low, medium, high", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_RejectsNumericReasoningValue()
    {
        var node = ValidConfigNode();
        node["Routes"]!["analysis-chat"]!["Reasoning"] = 1;

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()));

        Assert.Contains("Routes.analysis-chat.Reasoning", exception.Message, StringComparison.Ordinal);
        Assert.Contains("unsupported value '1'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("off, low, medium, high", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_RejectsReasoningOnEmbeddingRoute()
    {
        var node = ValidConfigNode();
        node["Routes"]!["memory-embed"]!["Reasoning"] = "off";

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()));

        Assert.Contains("Routes.memory-embed.Reasoning", exception.Message, StringComparison.Ordinal);
        Assert.Contains("embedding route", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_CurrentReleasesRequiresNonBlankServiceAndRelease()
    {
        var node = ValidConfigNode();
        node["CurrentReleases"] = new JsonObject
        {
            ["checkout"] = "",
            [""] = "2026.07.13.1"
        };

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()));

        Assert.Contains("CurrentReleases", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_RoleToolNotConfigured_FailsLoadValidation()
    {
        var node = ValidConfigNode();
        var roles = (JsonObject)node["Roles"]!;
        var analysis = (JsonObject)roles["analysis"]!;
        analysis["Tools"] = new JsonArray("unknown_tool");

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()));

        Assert.Contains("Roles.analysis.Tools", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_AllowedSourceWithoutRegisteredNormalizer_FailsLoadValidation()
    {
        var node = ValidConfigNode();
        var ingestion = (JsonObject)node["Ingestion"]!;
        ingestion["AllowedSources"] = new JsonArray("webhook");

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()));

        Assert.Contains("Ingestion.AllowedSources", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_RoleRouteMustBeChatRoute()
    {
        var node = ValidConfigNode();
        var roles = (JsonObject)node["Roles"]!;
        var analysis = (JsonObject)roles["analysis"]!;
        analysis["RouteId"] = "memory-embed";

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()));

        Assert.Contains("Roles.analysis.RouteId", exception.Message, StringComparison.Ordinal);
    }


    [Fact]
    public void Materialize_RateCapWithoutPositiveMax_FailsLoadValidation()
    {
        var node = ValidConfigNode();
        node["Rules"] = new JsonArray(new JsonObject
        {
            ["Type"] = "rate_cap",
            ["Tool"] = "*",
            ["Scope"] = "attempt",
            ["Max"] = 0
        });

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()));

        Assert.Contains("Rules.rate_cap.Max", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_PreconditionReferencingUnknownTool_FailsLoadValidation()
    {
        var node = ValidConfigNode();
        node["Rules"] = new JsonArray(new JsonObject
        {
            ["Type"] = "precondition",
            ["Tool"] = "memory_search",
            ["Scope"] = "attempt",
            ["RequiresSuccessfulToolResult"] = "unknown_tool"
        });

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()));

        Assert.Contains("Rules.RequiresSuccessfulToolResult", exception.Message, StringComparison.Ordinal);
    }


    [Fact]
    public void Materialize_FingerprintRuleWithUnknownInput_FailsLoadValidation()
    {
        var node = ValidConfigNode();
        var faultGrouping = (JsonObject)node["FaultGrouping"]!;
        faultGrouping["FingerprintRules"] = new JsonArray
        {
            new JsonObject
            {
                ["Id"] = "checkout-v2",
                ["Version"] = 2,
                ["Inputs"] = new JsonArray("UnsupportedInput")
            }
        };

        var exception = Assert.Throws<TriageConfigurationLoadException>(() => CreateMaterializer().Materialize("hash", node, ResolvedReferences()));

        Assert.Contains("FaultGrouping.FingerprintRules[0].Inputs", exception.Message, StringComparison.Ordinal);
    }
    [Fact]
    public void Materialize_NonPositiveSuppressionRuleWindow_FailsLoadValidation()
    {
        var node = ValidConfigNode();
        var faultGrouping = (JsonObject)node["FaultGrouping"]!;
        faultGrouping["SuppressionRules"] = new JsonArray
        {
            new JsonObject { ["Id"] = "checkout", ["SilenceWindowMinutes"] = 0, ["ServiceName"] = "checkout" }
        };

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash", node, ResolvedReferences()));

        Assert.Contains("FaultGrouping.SuppressionRules[0].SilenceWindowMinutes", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LookbackMinutes", "FaultGrouping.LookbackMinutes")]
    [InlineData("SilenceWindowMinutes", "FaultGrouping.SilenceWindowMinutes")]
    public void Materialize_NonPositiveFaultGroupingWindow_FailsLoadValidation(string settingName, string expectedMessage)
    {
        var node = ValidConfigNode();
        var faultGrouping = (JsonObject)node["FaultGrouping"]!;
        faultGrouping[settingName] = 0;

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_NonPositiveMassIssueMinNeighborCount_FailsLoadValidation()
    {
        var node = ValidConfigNode();
        var faultGrouping = (JsonObject)node["FaultGrouping"]!;
        var massIssue = (JsonObject)faultGrouping["MassIssue"]!;
        massIssue["MinNeighborCount"] = 0;

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()));

        Assert.Contains("FaultGrouping.MassIssue.MinNeighborCount", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_NegativeMaxReprompts_FailsLoadValidation()
    {
        var node = ValidConfigNode();
        var orchestrator = (JsonObject)node["Orchestrator"]!;
        var budget = (JsonObject)orchestrator["Budget"]!;
        budget["MaxReprompts"] = -1;

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()));

        Assert.Contains("Orchestrator.Budget.MaxReprompts", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fault")]
    [InlineData("bogus")]
    public void Materialize_PostMvpOrUnknownRuleScope_FailsLoadValidation(string scope)
    {
        var node = ValidConfigNode();
        node["Rules"] = new JsonArray(new JsonObject
        {
            ["Type"] = "rate_cap",
            ["Tool"] = "*",
            ["Scope"] = scope,
            ["Max"] = 50
        });

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()));

        Assert.Contains("Rules.rate_cap.Scope", exception.Message, StringComparison.Ordinal);
        Assert.Contains("attempt, job", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_MemorySearchRouteMustBeEmbeddingRoute()
    {
        var node = ValidConfigNode();
        var tools = (JsonObject)node["Tools"]!;
        var memorySearch = (JsonObject)tools["memory_search"]!;
        memorySearch["EmbeddingRouteId"] = "analysis-chat";

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()));

        Assert.Contains("Tools.memory_search.EmbeddingRouteId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_MemorySearchRequiresEmbeddingRouteId()
    {
        var node = ValidConfigNode();
        var tools = (JsonObject)node["Tools"]!;
        var memorySearch = (JsonObject)tools["memory_search"]!;
        memorySearch.Remove("EmbeddingRouteId");

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()));

        Assert.Contains("Tools.memory_search.EmbeddingRouteId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_MemorySearchCanOnlyBeGrantedToMemoryRole()
    {
        var node = ValidConfigNode();
        var roles = (JsonObject)node["Roles"]!;
        var analysis = (JsonObject)roles["analysis"]!;
        analysis["Tools"] = new JsonArray("memory_search");

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()));

        Assert.Contains("Roles.analysis.Tools", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_SourceLookupCanOnlyBeGrantedToSourceRole()
    {
        var node = ValidConfigNode();
        ((JsonObject)node["Tools"]!)["source_lookup"] = new JsonObject { ["Kind"] = "internal" };
        ((JsonObject)((JsonObject)node["Roles"]!)["analysis"]!)["Tools"] = new JsonArray("source_lookup");

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()));

        Assert.Contains("Roles.analysis.Tools", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_RemovingSourceRoleLeavesHostConfigurationValidAndToolDisabled()
    {
        var node = ValidConfigNode();
        ((JsonObject)node["Tools"]!)["source_lookup"] = new JsonObject { ["Kind"] = "internal" };

        var configuration = CreateMaterializer().Materialize("hash-1", node, ResolvedReferences());

        Assert.DoesNotContain("source", configuration.Roles.Keys);
        Assert.Contains("source_lookup", configuration.Tools.Keys);
    }

    [Fact]
    public void Materialize_SourceRoleWithoutGrantLeavesRoleToolSurfaceEmpty()
    {
        var node = ValidConfigNode();
        ((JsonObject)node["Tools"]!)["source_lookup"] = new JsonObject { ["Kind"] = "internal" };
        ((JsonObject)node["Roles"]!)["source"] = new JsonObject
        {
            ["RouteId"] = "analysis-chat",
            ["Instructions"] = "ref:instructions/source.md",
            ["Tools"] = new JsonArray(),
            ["OutputSchema"] = "ref:schemas/source.json"
        };

        var configuration = CreateMaterializer().Materialize("hash-1", node, ResolvedReferences());

        Assert.Empty(configuration.Roles["source"].Tools);
    }

    [Fact]
    public void Materialize_TicketSearchCanOnlyBeGrantedToTicketsRole()
    {
        var node = ValidConfigNode();
        ((JsonObject)node["Tools"]!)["ticket_search"] = new JsonObject { ["Kind"] = "internal" };
        ((JsonObject)((JsonObject)node["Roles"]!)["analysis"]!)["Tools"] = new JsonArray("ticket_search");

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()));

        Assert.Contains("Roles.analysis.Tools", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_RemovingTicketsRoleOrGrantDisablesTicketSurfaceWithoutHostFailure()
    {
        var node = ValidConfigNode();
        ((JsonObject)node["Tools"]!)["ticket_search"] = new JsonObject { ["Kind"] = "internal" };
        ((JsonObject)node["Roles"]!)["tickets"] = new JsonObject
        {
            ["RouteId"] = "analysis-chat",
            ["Instructions"] = "ref:instructions/tickets.md",
            ["Tools"] = new JsonArray(),
            ["OutputSchema"] = "ref:schemas/tickets.json"
        };

        var ungranted = CreateMaterializer().Materialize("hash-1", node, ResolvedReferences());
        ((JsonObject)node["Roles"]!).Remove("tickets");
        var removed = CreateMaterializer().Materialize("hash-2", node, ResolvedReferences());

        Assert.Empty(ungranted.Roles["tickets"].Tools);
        Assert.DoesNotContain("tickets", removed.Roles.Keys);
        Assert.Contains("ticket_search", removed.Tools.Keys);
    }

    [Fact]
    public void Materialize_AcceptsDisabledBackendTicketCreateDescriptorWithoutRoleGrant()
    {
        var node = ValidConfigNode();
        ((JsonObject)node["Tools"]!)[TicketCreateTool.ToolId] = new JsonObject
        {
            ["Kind"] = "external_action",
            ["Category"] = "ticket_create",
            ["LogicalTargetId"] = TicketCreateTool.LogicalTargetId,
            ["Mode"] = "disabled"
        };

        var configuration = CreateMaterializer().Materialize("hash-1", node, ResolvedReferences());

        Assert.Empty(configuration.Actions.AllowedTools);
        Assert.Equal("disabled", configuration.Tools[TicketCreateTool.ToolId].Mode);
        Assert.DoesNotContain(configuration.Roles.Values,
            role => role.Tools.Contains(TicketCreateTool.ToolId, StringComparer.Ordinal));
    }

    [Fact]
    public void Materialize_InvalidConfiguredRedactionPattern_FailsLoadValidation()
    {
        var node = ValidConfigNode();
        node["Redaction"] = JsonNode.Parse("""
            {
              "AttributeKeys": [],
              "Patterns": [{ "Name": "broken", "Pattern": "[" }],
              "UserIdentifierAttributes": []
            }
            """);

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("hash-1", node, ResolvedReferences()));

        Assert.Contains("Redaction.Patterns[0].Pattern", exception.Message, StringComparison.Ordinal);
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
            new AgentToolDescriptor("source_lookup", AgentToolCapability.ImmediateRead),
            new AgentToolDescriptor("ticket_search", AgentToolCapability.ImmediateRead),
            TicketCreateTool.Descriptor
        ]);
        return new TriageConfigurationMaterializer(new TriageConfigurationLoadValidator(registry, tools));
    }

    private static JsonObject ResolvedReferences() => new()
    {
        ["ref:instructions/orchestrator.md"] = "orchestrator body",
        ["ref:instructions/analysis.md"] = "analysis body",
        ["ref:schemas/analysis.json"] = "{ \"type\": \"object\" }",
        ["ref:instructions/source.md"] = "source body",
        ["ref:schemas/source.json"] = "{ \"type\": \"object\" }",
        ["ref:instructions/tickets.md"] = "tickets body",
        ["ref:schemas/tickets.json"] = "{ \"type\": \"object\" }"
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
