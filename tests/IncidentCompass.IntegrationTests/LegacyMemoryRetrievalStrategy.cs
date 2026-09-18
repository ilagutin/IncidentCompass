using System.Text.Json;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using IncidentCompass.Domain.Governance;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.IntegrationTests;

internal interface IMemoryRetrievalBenchmarkStrategy
{
    Task<IReadOnlyList<MemoryRetrievalQueryResult>> ExecuteAsync(
        MemoryRetrievalBenchmarkCorpus corpus,
        CancellationToken cancellationToken);
}

internal sealed class LegacyMemoryRetrievalStrategy(
    IEmbeddingClient embeddingClient,
    IMemoryRepository repository) : IMemoryRetrievalBenchmarkStrategy
{
    public async Task<IReadOnlyList<MemoryRetrievalQueryResult>> ExecuteAsync(
        MemoryRetrievalBenchmarkCorpus corpus,
        CancellationToken cancellationToken)
    {
        var results = new List<MemoryRetrievalQueryResult>(corpus.Queries.Count);
        foreach (var query in corpus.Queries)
        {
            var embedding = await embeddingClient.CreateEmbeddingAsync(
                new EmbeddingRequest(query.Text, corpus.EmbeddingModel, "memory-benchmark-legacy", EmbeddingInputKind.Query),
                cancellationToken);
            EnsureMockEmbedding(corpus, embedding);
            var vectorMatches = await repository.SearchAsync(
                new MemorySearchRequest(
                    corpus.TenantId,
                    embedding.Provider,
                    embedding.Model,
                    embedding.Vector.Count,
                    embedding.Vector,
                    corpus.TopK,
                    corpus.MinScore),
                cancellationToken);
            var finalMatches = MemorySearchLexicalFilter.Apply(query.Text, vectorMatches);
            results.Add(new MemoryRetrievalQueryResult(
                query.Id,
                finalMatches.Select(static match =>
                    new MemoryRetrievalMatch(match.MemoryItemId, match.ChunkId)).ToArray()));
        }

        return results;
    }

    private static void EnsureMockEmbedding(
        MemoryRetrievalBenchmarkCorpus corpus,
        EmbeddingResponse embedding)
    {
        if (embedding.Provider != "mock" ||
            embedding.Model != corpus.EmbeddingModel ||
            embedding.Vector.Count != corpus.EmbeddingDimensions)
        {
            throw new InvalidOperationException("Legacy benchmark strategy requires deterministic mock embeddings.");
        }
    }
}

/// <summary>
/// Runs every corpus query through the real <c>memory_search</c> tool. Each query's context carries the
/// trigger signal its fixture names, built by <see cref="MemoryRetrievalBenchmarkTriggerSignal" />, so the
/// reported band is decided against the production fault query; a corpus that predates signals runs
/// with none, and every band it reports is then <c>low</c>. <paramref name="signalFor" /> replaces that
/// signal for a leg that has to reproduce another rule, and is never a product setting.
/// </summary>
internal sealed class ProductionMemoryRetrievalStrategy(
    IImmediateAgentTool memorySearchTool,
    TriageConfiguration configuration,
    Func<MemoryRetrievalBenchmarkCorpus, MemoryRetrievalBenchmarkQuery, Signal?>? signalFor = null)
    : IMemoryRetrievalBenchmarkStrategy
{
    public async Task<IReadOnlyList<MemoryRetrievalQueryResult>> ExecuteAsync(
        MemoryRetrievalBenchmarkCorpus corpus,
        CancellationToken cancellationToken)
    {
        var benchmarkConfiguration = configuration with
        {
            CurrentReleases = corpus.CurrentReleases
        };
        var results = new List<MemoryRetrievalQueryResult>(corpus.Queries.Count);
        foreach (var query in corpus.Queries)
        {
            var arguments = JsonSerializer.SerializeToElement(new { query = query.Text });
            var validation = memorySearchTool.Validate(arguments);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException("Benchmark query failed memory_search validation.");
            }

            var execution = await memorySearchTool.ExecuteAsync(
                CreateContext(
                    benchmarkConfiguration,
                    corpus,
                    query,
                    signalFor is null
                        ? MemoryRetrievalBenchmarkTriggerSignal.For(corpus.TenantId, query)
                        : signalFor(corpus, query)),
                validation.SanitizedArguments,
                cancellationToken);
            if (execution.Status != ToolExecutionStatus.Succeeded)
            {
                throw new InvalidOperationException("Production memory_search benchmark execution failed.");
            }

            var matches = (execution.Artifacts ?? [])
                .Select(static draft => new MemoryRetrievalMatch(
                    Guid.Parse(draft.Payload["memoryItemId"]!.GetValue<string>()),
                    Guid.Parse(draft.Payload["chunkId"]!.GetValue<string>()),
                    draft.Payload["retrievalConfidence"]!.GetValue<string>()))
                .ToArray();
            results.Add(new MemoryRetrievalQueryResult(query.Id, matches));
        }

        return results;
    }

    private static AgentToolExecutionContext CreateContext(
        TriageConfiguration configuration,
        MemoryRetrievalBenchmarkCorpus corpus,
        MemoryRetrievalBenchmarkQuery query,
        Signal? triggerSignal)
    {
        var now = DateTimeOffset.UnixEpoch;
        var job = new TriageJob(
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            Guid.Parse("30000000-0000-0000-0000-000000000002"),
            TriageJobStatus.Processing,
            1,
            "memory-benchmark",
            now.AddHours(1),
            null,
            null,
            null,
            configuration.ConfigHash,
            now,
            now);
        return new AgentToolExecutionContext(
            job,
            configuration,
            "memory",
            "memory_search",
            corpus.TenantId,
            query.ServiceName)
        {
            ConversationId = Guid.Parse("30000000-0000-0000-0000-000000000003"),
            UserId = "memory-benchmark",
            CorrelationId = "memory-benchmark-" + query.Id,
            PolicyVersion = "benchmark-v1",
            TriggerSignal = triggerSignal
        };
    }
}
