using System.Globalization;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Shared scaffolding for the script-coverage and vector-only-fallback tests: a configuration whose
/// <c>memory_search</c> settings a test can vary, an execution context, candidate builders and the two
/// in-memory ports the tool depends on.
/// </summary>
internal static class MemorySearchToolTestSupport
{
    public const string ServiceName = "checkout-api";

    /// <summary>An English chunk holding the words the multilingual fixtures were translated from.</summary>
    public const string EnglishChunk = "checkout timeout inventory latency circuit breaker payments";

    public static TriageConfiguration Configuration(string? vectorOnlyFallback = null)
    {
        var configuration = TestTriageConfiguration.Create();
        var tools = new Dictionary<string, TriageToolSettings>(configuration.Tools, StringComparer.Ordinal);
        tools["memory_search"] = tools["memory_search"] with { VectorOnlyFallback = vectorOnlyFallback };
        return configuration with { Tools = tools };
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
