using System.Globalization;
using System.Text.Json;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Memory;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.UnitTests;

public sealed class MemorySearchRerankerTests
{
    [Fact]
    public void Rank_CurrentSameServicePrecedesHigherVectorStaleAndWrongService()
    {
        var current = Match(3, -1.0, "checkout-api", "2.4.0");
        var stale = Match(2, 1.0, "checkout-api", "2.3.0");
        var wrongService = Match(1, 1.0, "orders-api", "7.1.0");

        var ranked = MemorySearchReranker.Rank(
            "inventory latency circuit breaker",
            Configuration(),
            "checkout-api",
            [wrongService, stale, current],
            5);

        Assert.Equal([current.ChunkId, stale.ChunkId, wrongService.ChunkId],
            ranked.Select(static match => match.ChunkId));
    }

    [Fact]
    public void CreateFeatures_ComponentBoostRequiresExactNormalizedTokenOrPhrase()
    {
        var single = Match(1, component: "payments");
        var phrase = Match(2, component: "payment gateway");

        Assert.Equal(0.25, Features("payments retry timeout", single).ComponentBoost);
        Assert.Equal(0.25, Features("payment-gateway retry timeout", phrase).ComponentBoost);
        Assert.Equal(0, Features("payment retry timeout", single).ComponentBoost);
        Assert.Equal(0, Features("gateway retry timeout", phrase).ComponentBoost);
    }

    [Fact]
    public void CreateFeatures_EvidenceKindBoostRequiresCodeOwnedAlias()
    {
        var runbook = Match(1, kind: "runbook");
        var knownIncident = Match(2, kind: "known_incident");

        Assert.Equal(0.2, Features("checkout runbook timeout", runbook).EvidenceKindBoost);
        Assert.Equal(0.2, Features("known-incident checkout timeout", knownIncident).EvidenceKindBoost);
        Assert.Equal(0, Features("checkout timeout", runbook).EvidenceKindBoost);
        Assert.Equal(0, Features("incident checkout timeout", knownIncident).EvidenceKindBoost);
    }

    [Fact]
    public void Rank_UsesVectorThenChunkIdForCombinedScoreTies()
    {
        var lowerVectorWithComponent = Match(3, 0.5, component: "payments");
        var higherVector = Match(2, 0.75, component: "other");
        var lowerChunkId = Match(1, 0.75, component: "other");

        var ranked = MemorySearchReranker.Rank(
            "payments timeout",
            Configuration(currentReleases: false),
            "checkout-api",
            [lowerVectorWithComponent, higherVector, lowerChunkId],
            5);

        Assert.Equal([lowerChunkId.ChunkId, higherVector.ChunkId, lowerVectorWithComponent.ChunkId],
            ranked.Select(static match => match.ChunkId));
    }

    [Fact]
    public void Rank_FiltersWeakLexicalOverlapButKeepsStaleOnlyEvidence()
    {
        var stale = Match(1, 0.8, "checkout-api", "2.3.0", text: "legacy rollback procedure");
        var weak = Match(2, 0.99, "checkout-api", "2.4.0", text: "inventory unrelated note");

        var ranked = MemorySearchReranker.Rank(
            "legacy rollback procedure",
            Configuration(),
            "checkout-api",
            [weak, stale],
            5);

        Assert.Equal(stale.ChunkId, Assert.Single(ranked).ChunkId);
    }

    [Fact]
    public async Task ExecuteAsync_PerformsOneEmbeddingAndRepositoryAttemptAndReturnsFinalTopK()
    {
        var embedding = new CountingEmbeddingClient();
        var repository = new CountingMemoryRepository(
            Enumerable.Range(1, 8).Select(index => Match(index, 0.9)).ToArray());
        var tool = new MemorySearchTool(embedding, repository);
        var validation = tool.Validate(JsonSerializer.SerializeToElement(new { query = "checkout timeout" }));

        var result = await tool.ExecuteAsync(
            Context(Configuration()),
            validation.SanitizedArguments,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, embedding.CallCount);
        Assert.Equal(1, repository.SearchCount);
        Assert.Equal(20, repository.LastRequest!.CandidateCount);
        Assert.Equal(5, result.Artifacts!.Count);
    }

    [Fact]
    public async Task ExecuteAsync_MissingEmbeddingRouteFailsClosedBeforeEmbeddingOrMemorySearch()
    {
        var embedding = new CountingEmbeddingClient();
        var repository = new CountingMemoryRepository([]);
        var tool = new MemorySearchTool(embedding, repository);
        var configuration = Configuration() with
        {
            Routes = new Dictionary<string, TriageRouteSettings>(StringComparer.Ordinal)
        };
        var validation = tool.Validate(JsonSerializer.SerializeToElement(new { query = "checkout timeout" }));

        var exception = await Assert.ThrowsAsync<TriageGovernanceDeniedException>(
            () => tool.ExecuteAsync(
                Context(configuration),
                validation.SanitizedArguments,
                TestContext.Current.CancellationToken));

        Assert.Equal(TriageGovernanceDeniedException.MemorySearchRouteMissingCode, exception.ErrorCode);
        Assert.Contains("memory-embed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Tools.memory_search.EmbeddingRouteId", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, embedding.CallCount);
        Assert.Equal(0, repository.SearchCount);
    }

    [Fact]
    public void Validate_RejectsCallerOwnedRankingWeightsAndFilters()
    {
        var tool = new MemorySearchTool(
            new CountingEmbeddingClient(),
            new CountingMemoryRepository([]));

        var withWeight = tool.Validate(JsonSerializer.SerializeToElement(new
        {
            query = "checkout timeout",
            serviceWeight = 2
        }));
        var withFilter = tool.Validate(JsonSerializer.SerializeToElement(new
        {
            query = "checkout timeout",
            component = "payments"
        }));

        Assert.False(withWeight.IsValid);
        Assert.False(withFilter.IsValid);
    }

    private static MemorySearchRankingFeatures Features(string query, MemorySearchMatch match) =>
        MemorySearchReranker.CreateFeatures(query, Configuration(), "checkout-api", match);

    private static TriageConfiguration Configuration(bool currentReleases = true)
    {
        var configuration = TestTriageConfiguration.Create();
        return configuration with
        {
            CurrentReleases = currentReleases
                ? new Dictionary<string, string>(StringComparer.Ordinal) { ["checkout-api"] = "2.4.0" }
                : new Dictionary<string, string>(StringComparer.Ordinal)
        };
    }

    private static AgentToolExecutionContext Context(TriageConfiguration configuration)
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
            "checkout-api");
    }

    private static MemorySearchMatch Match(
        int id,
        double score = 0.9,
        string? service = "checkout-api",
        string? release = "2.4.0",
        string? component = null,
        string kind = "operational_note",
        string text = "checkout timeout inventory latency circuit breaker payments") => new(
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

    private sealed class CountingEmbeddingClient : IEmbeddingClient
    {
        public int CallCount { get; private set; }

        public Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new EmbeddingResponse([1f, 0f], request.Model, "mock", 2, request.CorrelationId));
        }
    }

    private sealed class CountingMemoryRepository(IReadOnlyList<MemorySearchMatch> matches) : IMemoryRepository
    {
        public int SearchCount { get; private set; }

        public MemorySearchRequest? LastRequest { get; private set; }

        public Task<IReadOnlyList<MemorySearchMatch>> SearchAsync(
            MemorySearchRequest request,
            CancellationToken cancellationToken)
        {
            SearchCount++;
            LastRequest = request;
            return Task.FromResult(matches);
        }

        public Task<bool> SeedItemExistsAsync(
            string owner,
            MemorySeedItem item,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReconcileSeedCorpusAsync(
            MemorySeedCorpus corpus,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
