using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Investigation.Jobs;
using static IncidentCompass.Infrastructure.ModelGateway.Mock.MockIncidentCompassReportScript;

namespace IncidentCompass.Infrastructure.ModelGateway.Mock;

internal static class MockIncidentCompassScripts
{
    /// <summary>
    /// The band memory_search gives an item nothing confirmed: one the relevance judge admitted
    /// without confirming, or, on a call no judge judged, every item. The mock reads the band rather
    /// than the message, so it stays correct under both.
    /// </summary>
    private const string UnconfirmedBand = "low";

    private const string NoMatchesReason = "no matches";

    private const string UnconfirmedOnlyReason = "no confirmed matches";

    public static AiModelResponse OrchestratorResponse(AiModelRequest request)
    {
        var toolResults = request.Messages
            .Where(static message => message.Role == AiMessageRole.Tool)
            .Select(static message => message.Content)
            .ToArray();
        if (toolResults.Length == 0)
        {
            return MockAiModelResponseFactory.CreateResponse(request, "Delegate analysis first.", [MockAiModelResponseFactory.ToolCall("incidentcompass-delegate-analysis-1", OrchestratorToolNames.Delegate, """
                {"role":"analysis","task":"Extract key facts and say whether memory context is needed."}
                """)]);
        }

        if (toolResults.Any(ContainsMemoryRoleResult))
        {
            return PublishAfterMemory(request, toolResults);
        }

        if (toolResults.Any(static value => value.Contains("\"needsDeeperContext\":true", StringComparison.OrdinalIgnoreCase)))
        {
            return MockAiModelResponseFactory.CreateResponse(request, "Delegate memory lookup.", [MockAiModelResponseFactory.ToolCall("incidentcompass-delegate-memory-1", OrchestratorToolNames.Delegate, """
                {"role":"memory","task":"Search memory for matching runbooks or known incidents using the trigger service, error type and message."}
                """)]);
        }

        var isNoise = toolResults.Any(static value => value.Contains("\"candidateClassification\":\"Noise\"", StringComparison.OrdinalIgnoreCase));
        return MockAiModelResponseFactory.CreateResponse(request, "Publish after analysis.", [MockAiModelResponseFactory.ToolCall(
            "incidentcompass-publish-report-1",
            OrchestratorToolNames.PublishReport,
            PublishArguments(
                "Completed",
                isNoise ? "Mock analysis closed the signal as noise." : "Mock analysis completed for the incident.",
                isNoise ? "Noise" : "SimpleKnownError",
                "Medium",
                [Evidence(FindPromptArtifactId(request, "TriggerSignal"))],
                [],
                isNoise ? "No incident action recommended." : "Review the affected service logs and confirm the failure path."))]);
    }

    private static AiModelResponse PublishAfterMemory(AiModelRequest request, IReadOnlyList<string> toolResults)
    {
        var matched = toolResults.Any(static value => value.Contains("\"matched\":true", StringComparison.OrdinalIgnoreCase));
        JsonObject[] evidence = matched
            ? [Evidence(FindMemoryArtifactId(toolResults) ?? FindPromptArtifactId(request, "TriggerSignal"), FindMemoryQuote(toolResults))]
            : [Evidence(FindPromptArtifactId(request, "TriggerSignal")), Evidence(FindPromptArtifactId(request, "NeighborSet"))];
        return MockAiModelResponseFactory.CreateResponse(request, "Publish after memory delegation.", [MockAiModelResponseFactory.ToolCall(
            "incidentcompass-publish-report-1",
            OrchestratorToolNames.PublishReport,
            matched
                ? PublishArguments(
                    "Completed",
                    "Mock memory lookup found relevant incident memory for the fault.",
                    "KnownIncident",
                    "Medium",
                    evidence,
                    [],
                    "Follow the retrieved checkout timeout runbook.")
                : PublishArguments(
                    "InsufficientEvidence",
                    "Mock memory lookup found no matching incident memory for the fault.",
                    "Unknown",
                    "Low",
                    evidence,
                    ["memory_search returned no matches"],
                    "Collect more service logs and dependency health data."))]);
    }

    public static AiModelResponse MemoryWorkerResponse(AiModelRequest request, bool hasToolResult)
    {
        if (!hasToolResult)
        {
            return MockAiModelResponseFactory.CreateResponse(request, "Search incident memory.", [MockAiModelResponseFactory.ToolCall(
                "incidentcompass-memory-search-1",
                "memory_search",
                MockIncidentCompassMemoryQuery.CreateSearchArguments(request))]);
        }

        var toolResult = request.Messages.Last(static message => message.Role == AiMessageRole.Tool).Content;
        return MockAiModelResponseFactory.CreateResponse(request, MemoryWorkerJson(toolResult), []);
    }

    public static bool IsIncidentCompassOrchestratorRequest(AiModelRequest request)
    {
        var toolNames = request.Tools?.Select(static tool => tool.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        return toolNames.SetEquals(OrchestratorToolNames.All);
    }

    public static bool IsAnalysisWorkerRequest(AiModelRequest request)
    {
        if (request.Tools is { Count: > 0 })
        {
            return false;
        }

        return request.Messages.Any(static message =>
            message.Role == AiMessageRole.System &&
            message.Content.Contains("analysis", StringComparison.OrdinalIgnoreCase) &&
            message.Content.Contains("worker", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsMemoryWorkerRequest(AiModelRequest request)
    {
        var toolNames = request.Tools?.Select(static tool => tool.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        return toolNames.SetEquals(["memory_search"]);
    }

    public static string AnalysisWorkerJson(string message)
    {
        var isNoise = message.Contains("noise", StringComparison.OrdinalIgnoreCase);
        var needsMemory = !isNoise && (message.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("NullReference", StringComparison.OrdinalIgnoreCase));
        var summary = needsMemory
            ? "The trigger signal should be checked against incident memory."
            : "The trigger signal contains enough grounded intake facts for a first classification.";

        return JsonSerializer.Serialize(new
        {
            keyFacts = new[] { summary, "The analysis worker used only grounded intake context." },
            candidateClassification = isNoise ? "Noise" : needsMemory ? "KnownIncident" : "SimpleKnownError",
            needsDeeperContext = needsMemory,
            rationale = needsMemory
                ? "The mock analysis requested memory context for this failure pattern."
                : "The mock analysis found a bounded known-error style failure from the trigger signal."
        });
    }

    /// <summary>
    /// Follows the shipped memory role instructions rather than trusting <c>matched</c>: an item banded
    /// <c>low</c> was admitted without being confirmed, so the mock drops it instead of quoting it, and
    /// returns the honest empty result when nothing else is left. Without this the mock profile would
    /// turn an unconfirmed cross-language hit into a KnownIncident report.
    /// <para>
    /// The band of an item the mock does keep is copied through, the way the role instructions now ask
    /// for. It is the orchestrator's only view of which documents can carry a <c>KnownIncident</c>
    /// classification, and a mock that dropped it would demonstrate a profile whose orchestrator can
    /// only meet that rule by being refused once.
    /// </para>
    /// </summary>
    private static string MemoryWorkerJson(string toolResult)
    {
        using var document = JsonDocument.Parse(toolResult);
        var root = document.RootElement;
        var matched = root.TryGetProperty("matched", out var matchedElement) && matchedElement.GetBoolean();
        if (!matched)
        {
            return NoMemoryMatchJson(ReadOptionalString(root, "noMatchReason") ?? NoMatchesReason);
        }

        var items = new JsonArray();
        foreach (var item in root.GetProperty("items").EnumerateArray())
        {
            var band = ReadOptionalString(item, "retrievalConfidence");
            if (string.Equals(band, UnconfirmedBand, StringComparison.Ordinal))
            {
                continue;
            }

            var memoryItem = new JsonObject
            {
                ["artifactId"] = ReadOptionalString(item, "artifactId") ?? string.Empty,
                ["title"] = ReadOptionalString(item, "title") ?? string.Empty,
                ["quote"] = ReadOptionalString(item, "quote") ?? string.Empty,
                ["score"] = item.TryGetProperty("score", out var score) && score.TryGetDouble(out var value) ? value : null
            };
            if (band is not null)
            {
                memoryItem["retrievalConfidence"] = band;
            }

            items.Add(memoryItem);
        }

        if (items.Count == 0)
        {
            return NoMemoryMatchJson(UnconfirmedOnlyReason);
        }

        return new JsonObject
        {
            ["matched"] = true,
            ["items"] = items
        }.ToJsonString();
    }

    private static string NoMemoryMatchJson(string noMatchReason) => JsonSerializer.Serialize(new
    {
        matched = false,
        items = Array.Empty<object>(),
        noMatchReason
    });

    private static bool ContainsMemoryRoleResult(string value)
    {
        return value.Contains("\"role\":\"memory\"", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }
}



