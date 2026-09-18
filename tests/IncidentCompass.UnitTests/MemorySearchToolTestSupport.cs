using System.Globalization;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Shared scaffolding for the script-coverage, vector-only-fallback and relevance-judge tests: a
/// configuration whose <c>memory_search</c> settings a test can vary, an execution context, candidate
/// builders and the in-memory ports the tool depends on.
/// </summary>
internal static class MemorySearchToolTestSupport
{
    public const string ServiceName = "checkout-api";

    /// <summary>An English chunk holding the words the multilingual fixtures were translated from.</summary>
    public const string EnglishChunk = "checkout timeout inventory latency circuit breaker payments";

    public static TriageConfiguration Configuration(
        string? vectorOnlyFallback = null,
        string? relevanceJudge = null,
        double? relevanceConfirmScore = null,
        double? relevanceFloorScore = null,
        bool currentReleases = false)
    {
        var configuration = TestTriageConfiguration.Create();
        var tools = new Dictionary<string, TriageToolSettings>(configuration.Tools, StringComparer.Ordinal);
        tools["memory_search"] = tools["memory_search"] with
        {
            VectorOnlyFallback = vectorOnlyFallback,
            RelevanceJudge = relevanceJudge,
            RelevanceConfirmScore = relevanceConfirmScore,
            RelevanceFloorScore = relevanceFloorScore
        };
        return configuration with
        {
            Tools = tools,
            CurrentReleases = currentReleases
                ? new Dictionary<string, string>(StringComparer.Ordinal) { [ServiceName] = "2.4.0" }
                : configuration.CurrentReleases
        };
    }

    public static AgentToolExecutionContext Context(TriageConfiguration configuration)
    {
        var now = DateTimeOffset.Parse("2026-01-15T00:00:00Z", CultureInfo.InvariantCulture);
        return new AgentToolExecutionContext(
            new TriageJob(
                Guid.Parse("90000000-0000-0000-0000-000000000001"),
                Guid.Parse("90000000-0000-0000-0000-000000000002"),
                TriageJobStatus.Processing,
                1,
                null,
                null,
                null,
                null,
                null,
                configuration.ConfigHash,
                now,
                now),
            configuration,
            "memory",
            "memory_search",
            "tenant-a",
            ServiceName);
    }

    public static MemorySearchMatch Match(
        int id,
        double score = 0.9,
        string text = EnglishChunk,
        string? service = ServiceName,
        string? release = null,
        string? component = null,
        string kind = "operational_note") => new(
            Guid.Parse($"10000000-0000-0000-0000-{id:D12}"),
            Guid.Parse($"20000000-0000-0000-0000-{id:D12}"),
            kind,
            $"memory-{id}.md",
            $"Memory {id}",
            0,
            text,
            score,
            service,
            component,
            release);
}

/// <summary>
/// A relevance judge whose answer the test writes: one score per candidate, in candidate order, the
/// last score repeating when there are more candidates than scores.
/// </summary>
internal sealed class ScriptedMemoryRelevanceJudge(params float[] scores) : IMemoryRelevanceJudge
{
    public int CallCount { get; private set; }

    public string? LastQuery { get; private set; }

    public IReadOnlyList<string> LastCandidates { get; private set; } = [];

    public Task<IReadOnlyList<float>> ScoreAsync(
        string query,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken)
    {
        CallCount++;
        LastQuery = query;
        LastCandidates = candidates;
        return Task.FromResult<IReadOnlyList<float>>(candidates
            .Select((_, index) => scores[Math.Min(index, scores.Length - 1)])
            .ToArray());
    }
}

/// <summary>
/// A relevance judge that breaks the port's one-score-per-candidate rule by answering one score short,
/// so the caller cannot attribute a score to a candidate.
/// </summary>
internal sealed class ShortScoringMemoryRelevanceJudge(float score) : IMemoryRelevanceJudge
{
    public Task<IReadOnlyList<float>> ScoreAsync(
        string query,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<float>>(
            Enumerable.Repeat(score, Math.Max(0, candidates.Count - 1)).ToArray());
}

/// <summary>
/// A relevance judge that refuses every call with a named code. It stands for both shapes the tool has
/// to tell apart: a host that is not running a judge at all, and a judge that is installed and then
/// fails or does not verify. The two are distinguished by the provider code, not the normalized one.
/// </summary>
internal sealed class RefusingMemoryRelevanceJudge(
    string errorCode,
    string providerErrorCode) : IMemoryRelevanceJudge
{
    public int CallCount { get; private set; }

    public Task<IReadOnlyList<float>> ScoreAsync(
        string query,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken)
    {
        CallCount++;
        throw new MemoryRelevanceJudgeException(
            "local-onnx",
            "The relevance judge refused the call.",
            errorCode: errorCode,
            providerErrorCode: providerErrorCode,
            failureKind: ProviderFailureKind.Unavailable);
    }
}

internal sealed class StubEmbeddingClient : IEmbeddingClient
{
    public Task<EmbeddingResponse> CreateEmbeddingAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new EmbeddingResponse([1f, 0f], request.Model, "mock", 2, request.CorrelationId));
}

internal sealed class StubMemoryRepository(IReadOnlyList<MemorySearchMatch> matches) : IMemoryRepository
{
    public Task<IReadOnlyList<MemorySearchMatch>> SearchAsync(
        MemorySearchRequest request,
        CancellationToken cancellationToken) => Task.FromResult(matches);

    public Task<bool> SeedItemExistsAsync(
        string owner,
        MemorySeedItem item,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task ReconcileSeedCorpusAsync(
        MemorySeedCorpus corpus,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<MemoryCorpusInventory> GetCorpusInventoryAsync(
        string tenantId,
        string owner,
        CancellationToken cancellationToken) => throw new NotSupportedException();
}
