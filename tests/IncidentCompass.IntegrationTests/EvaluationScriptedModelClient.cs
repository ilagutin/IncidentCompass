using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;

namespace IncidentCompass.IntegrationTests;

internal sealed class EvaluationScriptedModelClient : IAiModelClient
{
    /// <summary>The labelled benchmark query the memory worker sends for a checkout fault.</summary>
    public const string CheckoutQuery = "connection pool saturation";

    /// <summary>The labelled benchmark query the memory worker sends for the older-runbook checkout fault.</summary>
    public const string OlderRunbookQuery = "legacy rollback procedure";

    /// <summary>The query the memory worker sends for any other service, which no benchmark query labels.</summary>
    public const string UnrecognizedQuery = "unrecognized evaluation failure without matching service memory";

    public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
    {
        var toolNames = request.Tools?.Select(static tool => tool.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        var response = toolNames.SetEquals(["delegate", "publish_report"])
            ? Orchestrate(request)
            : toolNames.SetEquals(["memory_search"])
                ? RunMemory(request)
                : Analyze(request);
        return Task.FromResult(response);
    }

    private static AiModelResponse Orchestrate(AiModelRequest request)
    {
        var toolResults = request.Messages.Where(static message => message.Role == AiMessageRole.Tool).ToArray();
        if (toolResults.Length == 0)
        {
            return Respond(request, string.Empty, [Call("delegate-analysis", "delegate", new JsonObject
            {
                ["role"] = "analysis",
                ["task"] = "Summarize grounded facts and decide whether memory context is required."
            })]);
        }

        if (!toolResults.Any(static result => result.Content.Contains("\"role\":\"memory\"", StringComparison.Ordinal)))
        {
            return Respond(request, string.Empty, [Call("delegate-memory", "delegate", new JsonObject
            {
                ["role"] = "memory",
                ["task"] = "Search incident memory using the grounded service, error type and message."
            })]);
        }

        var memoryResult = toolResults.Last(static result => result.Content.Contains("\"role\":\"memory\"", StringComparison.Ordinal)).Content;
        using var memoryDocument = JsonDocument.Parse(memoryResult);
        var matched = memoryDocument.RootElement.GetProperty("matched").GetBoolean();
        var prompt = request.Messages.First(static message => message.Role == AiMessageRole.User).Content;
        var evidence = matched ? MemoryEvidence(memoryDocument.RootElement) : IntakeEvidence(prompt);
        var report = matched
            ? Report(
                "Completed",
                "Checkout inventory timeout matches retrieved incident memory and requires runbook review.",
                "KnownIncident",
                // The memory worker only queries checkout-api documentation that targets a release
                // older than the configured current release, so a single cited document is Stale.
                "StaleOnly",
                evidence,
                ["The cited checkout document targets an older release and must be revalidated."],
                "Review the cited evidence and verify current inventory latency before mitigation.")
            : Report(
                "InsufficientEvidence",
                "Available evidence does not identify a supported diagnosis.",
                "Unknown",
                "Missing",
                evidence,
                ["memory_search returned no relevant context"],
                "Collect dependency health, trace and service log evidence before deciding.");
        return Respond(request, string.Empty, [Call("publish-report", "publish_report", new JsonObject { ["report_json"] = report })]);
    }

    private static AiModelResponse Analyze(AiModelRequest request)
    {
        var prompt = request.Messages.LastOrDefault(static message => message.Role == AiMessageRole.User)?.Content ?? string.Empty;
        var content = new JsonObject
        {
            ["keyFacts"] = new JsonArray("Only backend-provided incident context was considered."),
            ["candidateClassification"] = prompt.Contains("checkout-api", StringComparison.Ordinal) ? "KnownIncident" : "Unknown",
            ["needsDeeperContext"] = true,
            ["rationale"] = "Memory context is required before publication."
        }.ToJsonString();
        return Respond(request, content, []);
    }

    private static AiModelResponse RunMemory(AiModelRequest request)
    {
        var prompt = request.Messages.First(static message => message.Role == AiMessageRole.User).Content;
        if (!request.Messages.Any(static message => message.Role == AiMessageRole.Tool))
        {
            if (prompt.Contains("PromptInjectionAttempt", StringComparison.Ordinal))
            {
                return Respond(request, string.Empty, [Call("forbidden-ticket-search", "ticket_search", new JsonObject())]);
            }

            return Respond(request, string.Empty, [Call("memory-search", "memory_search", new JsonObject { ["query"] = MemoryQuery(prompt) })]);
        }

        var toolResult = request.Messages.Last(static message => message.Role == AiMessageRole.Tool).Content;
        using var document = JsonDocument.Parse(toolResult);
        var root = document.RootElement;
        var matched = root.GetProperty("matched").GetBoolean();
        var items = new JsonArray();
        if (matched)
        {
            foreach (var item in root.GetProperty("items").EnumerateArray())
            {
                // The role instructions drop an item nothing confirmed, whether the relevance judge
                // admitted it unconfirmed or the vector-only fallback returned it, so the scripted
                // worker does not quote it either.
                if (item.GetProperty("retrievalConfidence").GetString() == "low")
                {
                    continue;
                }

                // The shipped memory schema has no null-typed properties: an absent
                // targetCurrentRelease is omitted, not emitted as null.
                var memoryItem = new JsonObject
                {
                    ["artifactId"] = item.GetProperty("artifactId").GetString(),
                    ["title"] = item.GetProperty("title").GetString(),
                    ["quote"] = item.GetProperty("quote").GetString(),
                    ["score"] = item.GetProperty("score").GetDouble(),
                    ["documentationStatus"] = item.GetProperty("documentationStatus").GetString(),
                    // Copied through the way the role instructions now ask for: it is what tells the
                    // orchestrator which documents can carry a KnownIncident classification.
                    ["retrievalConfidence"] = item.GetProperty("retrievalConfidence").GetString()
                };
                if (item.GetProperty("targetCurrentRelease").ValueKind == JsonValueKind.String)
                {
                    memoryItem["targetCurrentRelease"] = item.GetProperty("targetCurrentRelease").GetString();
                }

                items.Add(memoryItem);
            }
        }

        var confirmed = matched && items.Count > 0;
        var output = new JsonObject
        {
            ["matched"] = confirmed,
            ["items"] = items
        };
        if (!confirmed)
        {
            output["noMatchReason"] = matched ? "no confirmed matches" : "no matches";
        }

        return Respond(request, output.ToJsonString(), []);
    }

    // Each query is a labelled query of the shared memory retrieval benchmark, whose relevant chunk
    // this test already asserts is retrieved. The lexical reranking filter keeps only that document,
    // so the top-ranked checkout-api match is deterministic and targets an older release.
    private static string MemoryQuery(string prompt) =>
        prompt.Contains("checkout-api", StringComparison.Ordinal)
            ? prompt.Contains("older runbook pattern", StringComparison.Ordinal)
                ? OlderRunbookQuery
                : CheckoutQuery
            : UnrecognizedQuery;

    private static JsonObject Report(
        string status,
        string summary,
        string classification,
        string documentationFit,
        JsonArray evidence,
        IReadOnlyList<string> limitations,
        string nextAction) => new()
        {
            ["status"] = status,
            ["summary"] = summary,
            ["classification"] = classification,
            ["confidence"] = status == "Completed" ? "Medium" : "Low",
            ["documentationFit"] = documentationFit,
            ["evidence"] = evidence,
            ["limitations"] = new JsonArray(limitations.Select(static value => JsonValue.Create(value) as JsonNode).ToArray()),
            ["recommendedNextAction"] = nextAction
        };

    private static JsonArray MemoryEvidence(JsonElement memoryResult)
    {
        // The delegate result serializes MemoryWorkerOutputItem records with their declared
        // PascalCase names, so read either casing.
        var item = memoryResult.GetProperty("items")[0];
        return new JsonArray(new JsonObject
        {
            ["referenceId"] = ReadStringEitherCase(item, "artifactId"),
            ["quote"] = ReadStringEitherCase(item, "quote")
        });
    }

    private static string? ReadStringEitherCase(JsonElement element, string camelCaseName)
    {
        if (element.TryGetProperty(camelCaseName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        var pascalCaseName = char.ToUpperInvariant(camelCaseName[0]) + camelCaseName[1..];
        return element.TryGetProperty(pascalCaseName, out value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static JsonArray IntakeEvidence(string prompt) =>
        new(
            new JsonObject { ["referenceId"] = FindArtifact(prompt, "TriggerSignal") },
            new JsonObject { ["referenceId"] = FindArtifact(prompt, "NeighborSet") });

    private static string FindArtifact(string prompt, string kind)
    {
        var line = prompt.Split('\n').First(value => value.Contains("kind=" + kind, StringComparison.Ordinal));
        var start = line.IndexOf("artifact:", StringComparison.Ordinal) + "artifact:".Length;
        return line[start..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    private static AiToolCall Call(string id, string name, JsonObject arguments) =>
        new(id, name, "v1", JsonSerializer.SerializeToElement(arguments));

    private static AiModelResponse Respond(AiModelRequest request, string content, IReadOnlyList<AiToolCall> calls) =>
        new(content, request.Model, "evaluation-script", new AiModelUsage(10, 10, 20), request.CorrelationId, calls);
}
