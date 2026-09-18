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

/// <summary>
/// The governed memory read tool. <paramref name="relevanceJudge" /> is optional because it is a
/// deployment shape: only the Worker composes a judge, and a Worker that names no judge model
/// directory composes one that reports itself unavailable. Either way the tool keeps working and says
/// in its output that nothing judged the result.
/// </summary>
internal sealed class MemorySearchTool(
    IEmbeddingClient embeddingClient,
    IMemoryRepository memoryRepository,
    IMemoryRelevanceJudge? relevanceJudge = null) : IImmediateAgentTool
{
    /// <summary>
    /// The tool's registered name, which is also the tail of the <c>tool:</c> domain reference its
    /// durable <c>ToolResult</c> artifact carries. Named here so the publication rule that has to
    /// recognize such an artifact does not match on a literal of its own.
    /// </summary>
    public const string ToolId = "memory_search";

    private const int DefaultTopK = 5;
    private const double DefaultMinScore = 0.25;
    private const int MaxTopK = 20;
    private const int MaxQuoteLength = 500;
    private const string MatchesFoundMessage = MemorySearchMessage.MatchesFound;
    private const string NoMatchesMessage = MemorySearchMessage.NoMatches;
    private const string VectorOnlyMatchesMessage = MemorySearchMessage.VectorOnlyMatches;
    private const string RelatedMatchesMessage = MemorySearchMessage.RelatedMatches;

    public AiToolDefinition Definition { get; } = new(
        ToolId,
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

        // Bounded because the judge scores the query and the candidate inside one token window: an
        // unbounded query is one the judge never sees in full, and the bound is derived from that
        // window rather than chosen. See MemorySearchQueryBound.
        var query = queryElement.GetString()!.Trim();
        if (query.Length > MemorySearchQueryBound.MaxQueryCharacters)
        {
            return ToolValidationResult.Invalid("invalid_arguments", MemorySearchQueryBound.TooLongRefusal);
        }

        return ToolValidationResult.Valid(CanonicalJsonSerializer.ToElement(new JsonObject
        {
            ["query"] = query
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
            new EmbeddingRequest(
                query,
                route.Model,
                context.Job.Id.ToString(),
                EmbeddingInputKind.Query,
                route.ProviderId),
            cancellationToken);

        var topK = NormalizeTopK(toolSettings.TopK);
        var candidates = await memoryRepository.SearchAsync(
            new MemorySearchRequest(context.TenantId, embedding.Provider, embedding.Model,
                embedding.Vector.Count, embedding.Vector, CalculateCandidateCount(topK), NormalizeMinScore(toolSettings.MinScore)),
            cancellationToken);
        var judgeSettings = MemoryRelevanceJudgeSetting.Resolve(toolSettings);
        var judgement = await MemoryRelevanceJudgePass.JudgeAsync(
            relevanceJudge,
            query,
            candidates,
            judgeSettings,
            cancellationToken);
        var admitted = MemorySearchReranker.Rank(
            query,
            context.Configuration,
            context.FaultServiceName,
            candidates,
            topK,
            MemorySearchVectorOnlyFallbackSetting.Resolve(toolSettings.VectorOnlyFallback),
            judgement);

        // Admission and order above answer the role's query. The band answers whether each returned
        // document describes the incident the backend received, so it is decided against the fault
        // query built from the trigger signal, which the role cannot reword.
        var confirmation = await MemoryFaultConfirmation.ConfirmAsync(
            relevanceJudge,
            MemoryFaultQuery.For(context.TriggerSignal),
            admitted,
            judgement,
            judgeSettings,
            cancellationToken);
        var ranked = confirmation.Matches;

        var drafts = ranked
            .Select(match => CreateRetrievedDraft(context, embedding, match))
            .ToArray();
        return new ToolExecutionResult(
            ToolExecutionStatus.Succeeded,
            CreateOutput(context, ranked, drafts, confirmation.Limitation ?? judgement.Limitation),
            Artifacts: drafts);
    }

    // The throwing form is the right one here: the only segment is a Guid rendered by the runtime,
    // so it is always 36 characters of hexadecimal and hyphens. It cannot break a rule, and a
    // refusal would mean the runtime's own formatting changed rather than that a connector supplied
    // something unexpected.
    private static ToolArtifactDraft CreateRetrievedDraft(AgentToolExecutionContext context, EmbeddingResponse embedding, MemorySearchRankedMatch ranked) =>
        new(
            ArtifactKind.RetrievedItem,
            ArtifactDomainRef.Create("memory_item", ranked.Match.MemoryItemId.ToString()),
            CreateRetrievedPayload(context, embedding, ranked));

    private static JsonObject CreateRetrievedPayload(AgentToolExecutionContext context, EmbeddingResponse embedding, MemorySearchRankedMatch ranked)
    {
        var match = ranked.Match;
        var documentation = MemoryDocumentationStatusEvaluator.Assess(context.Configuration, context.FaultServiceName, match);
        return new JsonObject
        {
            ["memoryItemId"] = match.MemoryItemId.ToString(),
            ["chunkId"] = match.ChunkId.ToString(),
            ["kind"] = match.Kind,
            ["source"] = match.Source,
            ["title"] = match.Title,
            ["chunkPosition"] = match.ChunkPosition,
            ["headingPath"] = match.HeadingPath,
            ["quote"] = CreateQuote(match.Text),
            ["score"] = Math.Round(match.Score, 6),
            ["judgeScore"] = RoundJudgeScore(ranked.JudgeScore),
            [MemoryRetrievalConfidence.ConfirmationScorePropertyName] = RoundJudgeScore(ranked.ConfirmationScore),
            [MemoryRetrievalConfidence.PayloadPropertyName] = ranked.RetrievalConfidence,
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

    private static JsonElement CreateOutput(
        AgentToolExecutionContext context,
        IReadOnlyList<MemorySearchRankedMatch> ranked,
        ToolArtifactDraft[] drafts,
        string? limitation)
    {
        var items = new JsonArray();
        for (var i = 0; i < ranked.Count; i++)
        {
            var match = ranked[i].Match;
            var documentation = MemoryDocumentationStatusEvaluator.Assess(context.Configuration, context.FaultServiceName, match);
            items.Add(new JsonObject
            {
                ["artifactId"] = drafts[i].Id.ToString(),
                ["memoryItemId"] = match.MemoryItemId.ToString(),
                ["chunkPosition"] = match.ChunkPosition,
                ["headingPath"] = match.HeadingPath,
                ["title"] = match.Title,
                ["kind"] = match.Kind,
                ["source"] = match.Source,
                ["quote"] = CreateQuote(match.Text),
                ["score"] = Math.Round(match.Score, 6),
                ["judgeScore"] = RoundJudgeScore(ranked[i].JudgeScore),
                [MemoryRetrievalConfidence.ConfirmationScorePropertyName] = RoundJudgeScore(ranked[i].ConfirmationScore),
                [MemoryRetrievalConfidence.PayloadPropertyName] = ranked[i].RetrievalConfidence,
                ["serviceName"] = match.ServiceName,
                ["component"] = match.Component,
                ["release"] = match.ReleaseName,
                ["targetCurrentRelease"] = documentation.TargetCurrentRelease,
                ["documentationStatus"] = documentation.Status.ToString()
            });
        }

        var matched = ranked.Count > 0;
        return CanonicalJsonSerializer.ToElement(new JsonObject
        {
            ["matched"] = matched,
            ["message"] = ResolveMessage(ranked),
            ["limitation"] = limitation,
            ["items"] = items,
            ["noMatchReason"] = matched ? null : NoMatchesMessage
        });
    }

    /// <summary>
    /// An unconfirmed result is reported under its own sentence so the role can tell it apart from a
    /// confirmed one without reading each item's band: <c>matches found</c> is said exactly when at
    /// least one item is confirmed, which needs a relevance judge. There are two unconfirmed sentences
    /// because there are two different unconfirmed sets: the vector-only fallback set, which only the
    /// unjudged path can produce, and every other set, nothing in which was confirmed.
    /// </summary>
    private static string ResolveMessage(IReadOnlyList<MemorySearchRankedMatch> ranked)
    {
        if (ranked.Count == 0)
        {
            return NoMatchesMessage;
        }

        if (ranked.Any(static match => MemoryRetrievalConfidence.ConfirmsMatch(match.RetrievalConfidence)))
        {
            return MatchesFoundMessage;
        }

        return ranked.Any(static match => match.VectorOnly)
            ? VectorOnlyMatchesMessage
            : RelatedMatchesMessage;
    }

    /// <summary>
    /// The judge's scores are reported beside the vector score, never in place of it: <c>score</c> is
    /// the same cosine similarity it has always been, and <c>judgeScore</c> and
    /// <c>confirmationScore</c> are each null when no judge scored them.
    /// </summary>
    private static JsonValue? RoundJudgeScore(double? judgeScore) =>
        judgeScore is { } score ? JsonValue.Create(Math.Round(score, 6)) : null;

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
