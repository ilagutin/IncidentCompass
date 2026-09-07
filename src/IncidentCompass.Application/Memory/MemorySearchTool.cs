using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Core.Text;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Governance.Validation;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Domain.Governance;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.Memory;

internal sealed class MemorySearchTool(IEmbeddingClient embeddingClient, IMemoryRepository memoryRepository, TimeProvider timeProvider) : IImmediateAgentTool
{
    private const int DefaultTopK = 5;
    private const double DefaultMinScore = 0.25;
    private const int MaxTopK = 20;
    private const int MaxQuoteLength = 500;

    public AiToolDefinition Definition { get; } = new(
        "memory_search",
        "Search tenant-scoped incident memory for matching runbooks and known incidents.",
        "v1",
        CanonicalJsonSerializer.ToElement(new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["query"] = new JsonObject { ["type"] = "string" }
            },
            ["required"] = new JsonArray("query")
        }));

    public ToolValidationResult Validate(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return ToolValidationResult.Invalid("invalid_arguments", "memory_search arguments must be an object.");
        }

        foreach (var property in arguments.EnumerateObject())
        {
            if (!string.Equals(property.Name, "query", StringComparison.Ordinal))
            {
                return ToolValidationResult.Invalid("invalid_arguments", "memory_search accepts only query.");
            }
        }

        if (!arguments.TryGetProperty("query", out var queryElement) ||
            queryElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(queryElement.GetString()))
        {
            return ToolValidationResult.Invalid("invalid_arguments", "memory_search requires a non-empty query.");
        }

        return ToolValidationResult.Valid(CanonicalJsonSerializer.ToElement(new JsonObject
        {
            ["query"] = queryElement.GetString()!.Trim()
        }));
    }

    public async Task<ToolExecutionResult> ExecuteAsync(
        AgentToolExecutionContext context,
        JsonElement sanitizedArguments,
        CancellationToken cancellationToken)
    {
        var query = sanitizedArguments.GetProperty("query").GetString()!;
        var toolSettings = context.Configuration.Tools[context.ToolName];
        var routeId = toolSettings.EmbeddingRouteId ??
            throw new InvalidOperationException("memory_search is missing EmbeddingRouteId.");
        if (!context.Configuration.Routes.TryGetValue(routeId, out var route))
        {
            throw new TriageGovernanceDeniedException(
                TriageGovernanceDeniedException.MemorySearchRouteMissingCode,
                "memory_search embedding route '" + routeId +
                "' is not configured; fix Tools.memory_search.EmbeddingRouteId.");
        }

        var embedding = await embeddingClient.CreateEmbeddingAsync(
            new EmbeddingRequest(query, route.Model, context.Job.Id.ToString()),
            cancellationToken);

        var topK = NormalizeTopK(toolSettings.TopK);
        var candidates = await memoryRepository.SearchAsync(
            new MemorySearchRequest(context.TenantId, embedding.Provider, embedding.Model,
                embedding.Vector.Count, embedding.Vector, CalculateCandidateCount(topK), NormalizeMinScore(toolSettings.MinScore)),
            cancellationToken);
        var matches = MemorySearchReranker.Rank(
            query,
            context.Configuration,
            context.FaultServiceName,
            candidates,
            topK);

        var artifacts = matches
            .Select(match => CreateRetrievedArtifact(context, embedding, match))
            .ToArray();
        return new ToolExecutionResult(
            ToolExecutionStatus.Succeeded,
            CreateOutput(context, matches, artifacts),
            Artifacts: artifacts);
    }

    private TriageArtifact CreateRetrievedArtifact(AgentToolExecutionContext context, EmbeddingResponse embedding, MemorySearchMatch match)
    {
        var payload = CreateRetrievedPayload(context, embedding, match);
        var canonicalPayload = CanonicalJsonSerializer.Canonicalize(payload);
        return new TriageArtifact(
            Guid.NewGuid(),
            context.Job.Id,
            context.Job.Attempt,
            ArtifactKind.RetrievedItem,
            "memory_item:" + match.MemoryItemId,
            CanonicalJsonSerializer.ToElement(payload),
            CanonicalJsonSerializer.ComputeSha256Hex(canonicalPayload),
            timeProvider.GetUtcNow());
    }

    private static JsonObject CreateRetrievedPayload(AgentToolExecutionContext context, EmbeddingResponse embedding, MemorySearchMatch match)
    {
        var documentation = MemoryDocumentationStatusEvaluator.Assess(context.Configuration, context.FaultServiceName, match);
        return new JsonObject
        {
            ["memoryItemId"] = match.MemoryItemId.ToString(),
            ["chunkId"] = match.ChunkId.ToString(),
            ["kind"] = match.Kind,
            ["source"] = match.Source,
            ["title"] = match.Title,
            ["chunkPosition"] = match.ChunkPosition,
            ["quote"] = CreateQuote(match.Text),
            ["score"] = Math.Round(match.Score, 6),
            ["retrievalConfidence"] = MemoryRetrievalConfidence.Band(match.Score),
            ["serviceName"] = match.ServiceName,
            ["component"] = match.Component,
            ["release"] = match.ReleaseName,
            ["targetCurrentRelease"] = documentation.TargetCurrentRelease,
            ["documentationStatus"] = documentation.Status.ToString(),
            ["embeddingProvider"] = embedding.Provider,
            ["embeddingModel"] = embedding.Model,
            ["embeddingDimensions"] = embedding.Vector.Count
        };
    }

    private static JsonElement CreateOutput(AgentToolExecutionContext context, IReadOnlyList<MemorySearchMatch> matches, TriageArtifact[] artifacts)
    {
        var items = new JsonArray();
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var documentation = MemoryDocumentationStatusEvaluator.Assess(context.Configuration, context.FaultServiceName, match);
            items.Add(new JsonObject
            {
                ["artifactId"] = artifacts[i].Id.ToString(),
                ["memoryItemId"] = match.MemoryItemId.ToString(),
                ["title"] = match.Title,
                ["kind"] = match.Kind,
                ["source"] = match.Source,
                ["quote"] = CreateQuote(match.Text),
                ["score"] = Math.Round(match.Score, 6),
                ["retrievalConfidence"] = MemoryRetrievalConfidence.Band(match.Score),
                ["serviceName"] = match.ServiceName,
                ["component"] = match.Component,
                ["release"] = match.ReleaseName,
                ["targetCurrentRelease"] = documentation.TargetCurrentRelease,
                ["documentationStatus"] = documentation.Status.ToString()
            });
        }

        return CanonicalJsonSerializer.ToElement(new JsonObject
        {
            ["matched"] = matches.Count > 0,
            ["message"] = matches.Count > 0 ? "matches found" : "no matches",
            ["items"] = items,
            ["noMatchReason"] = matches.Count > 0 ? null : "no matches"
        });
    }

    private static string CreateQuote(string text)
    {
        var normalized = text.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        return TextTruncator.Truncate(normalized, MaxQuoteLength);
    }

    private static int NormalizeTopK(int? topK)
    {
        return Math.Clamp(topK ?? DefaultTopK, 1, MaxTopK);
    }

    private static double NormalizeMinScore(double? minScore)
    {
        return Math.Clamp(minScore ?? DefaultMinScore, -1.0, 1.0);
    }

    private static int CalculateCandidateCount(int topK)
    {
        return Math.Min(100, topK * 4);
    }
}
