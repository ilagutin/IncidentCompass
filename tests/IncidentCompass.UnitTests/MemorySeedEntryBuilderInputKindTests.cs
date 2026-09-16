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
        var builder = new MemorySeedEntryBuilder(
            embeddingClient, new UnreadMemoryRepository(),
            new MemoryDocumentChunker(new CharacterEstimateChunkTokenCounter(), new MemoryChunkingOptions()));

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

    [Fact]
    public async Task BuildAsync_ALineThatCannotFitIsRefusedNamingTheSeedSourceWithoutItsText()
    {
        var embeddingClient = new RecordingEmbeddingClient();
        var builder = new MemorySeedEntryBuilder(
            embeddingClient, new UnreadMemoryRepository(),
            new MemoryDocumentChunker(
                new CharacterEstimateChunkTokenCounter(),
                new MemoryChunkingOptions { MaxTokens = 40, OverlapTokens = 0, MinTokens = 4 }));
        var secretLine = "payment-token-" + new string('q', 400);

        var exception = await Assert.ThrowsAsync<MemorySeedDocumentRefusedException>(() => builder.BuildAsync(
            new MemorySeedFile(
                "runbook",
                "runbooks/oversized-line.md",
                "Oversized line",
                "# Oversized line\n" + secretLine,
                [],
                ServiceName: null,
                Component: null,
                ReleaseName: null),
            new MemorySeedOptions(),
            new MemoryEmbeddingRoute("memory-embed", "local-embed", "intfloat/multilingual-e5-small"),
            forceEmbedding: true,
            TestContext.Current.CancellationToken));

        Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Equal("runbooks/oversized-line.md", exception.SeedSource);
        Assert.Contains("'runbooks/oversized-line.md'", exception.Message);
        Assert.Contains("complete line exceed", exception.Message);
        Assert.DoesNotContain("payment-token", exception.Message);
        Assert.DoesNotContain("qqqq", exception.Message);
        Assert.Empty(embeddingClient.Requests);
    }

    [Fact]
    public async Task BuildAsync_ACounterFailureIsNotReportedAsANamedFileRefusal()
    {
        var embeddingClient = new RecordingEmbeddingClient();
        var builder = new MemorySeedEntryBuilder(
            embeddingClient, new UnreadMemoryRepository(),
            new MemoryDocumentChunker(new UnavailableChunkTokenCounter(), new MemoryChunkingOptions()));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(
            new MemorySeedFile(
                "runbook",
                "runbooks/checkout-timeouts.md",
                "Checkout timeouts",
                "Checkout timeouts: check the payment service latency first.",
                [],
                ServiceName: null,
                Component: null,
                ReleaseName: null),
            new MemorySeedOptions(),
            new MemoryEmbeddingRoute("memory-embed", "local-embed", "intfloat/multilingual-e5-small"),
            forceEmbedding: true,
            TestContext.Current.CancellationToken));

        Assert.Equal(UnavailableChunkTokenCounter.Message, exception.Message);
        Assert.Null(exception.InnerException);
        Assert.Empty(embeddingClient.Requests);
    }

    private sealed class UnavailableChunkTokenCounter : IMemoryChunkTokenCounter
    {
        public const string Message = "The memory chunk tokenizer is not available.";

        public string Kind => "exact";

        public Task InitializeAsync(MemoryChunkingOptions options, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public int CountTokens(string text) => throw new InvalidOperationException(Message);

        public int CountOverlapTokens(string text) => throw new InvalidOperationException(Message);
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
