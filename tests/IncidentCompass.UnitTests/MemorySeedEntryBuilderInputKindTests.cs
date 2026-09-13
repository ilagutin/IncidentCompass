using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.Memory;

namespace IncidentCompass.UnitTests;

/// <summary>
/// A seed file becomes a passage stored in the corpus, so the seed pass has to say so on every
/// embedding request; the adapter, not this builder, owns what a passage looks like to its model.
/// </summary>
public sealed class MemorySeedEntryBuilderInputKindTests
{
    [Fact]
    public async Task BuildAsync_EmbedsTheSeedFileAsAPassageOnTheRouteProvider()
    {
        var embeddingClient = new RecordingEmbeddingClient();
        var builder = new MemorySeedEntryBuilder(embeddingClient, new UnreadMemoryRepository());

        await builder.BuildAsync(
            new MemorySeedFile(
                "runbook",
                "runbooks/checkout-timeouts.md",
                "Checkout timeouts",
                "Checkout timeouts: check the payment service latency first.",
                ["checkout"],
                "checkout-api",
                Component: null,
                ReleaseName: null),
            new MemorySeedOptions(),
            new MemoryEmbeddingRoute("memory-embed", "local-embed", "intfloat/multilingual-e5-small"),
            forceEmbedding: true,
            TestContext.Current.CancellationToken);

        var request = Assert.Single(embeddingClient.Requests);
        Assert.Equal(EmbeddingInputKind.Passage, request.Kind);
        Assert.Equal("local-embed", request.ProviderId);
        Assert.Equal("intfloat/multilingual-e5-small", request.Model);
    }

    private sealed class RecordingEmbeddingClient : IEmbeddingClient
    {
        public List<EmbeddingRequest> Requests { get; } = [];

        public Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new EmbeddingResponse([1f, 0f], request.Model, "mock", 2, request.CorrelationId));
        }
    }

    /// <summary>
    /// A forced build never asks the repository whether the seed is already published, so every
    /// member throws: a read here would mean the builder stopped honouring the force flag.
    /// </summary>
    private sealed class UnreadMemoryRepository : IMemoryRepository
    {
        public Task<IReadOnlyList<MemorySearchMatch>> SearchAsync(
            MemorySearchRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> SeedItemExistsAsync(
            string owner,
            MemorySeedItem item,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReconcileSeedCorpusAsync(
            MemorySeedCorpus corpus,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MemoryCorpusInventory> GetCorpusInventoryAsync(
            string tenantId,
            string owner,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
