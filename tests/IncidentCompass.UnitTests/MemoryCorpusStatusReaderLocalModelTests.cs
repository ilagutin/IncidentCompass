using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.Memory;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The Api composes corpus status without the local model, so for a route served by that model it
/// compares only the id part of the corpus model and folds in what the Worker last recorded.
/// </summary>
public sealed class MemoryCorpusStatusReaderLocalModelTests
{
    private static readonly Guid Generation = Guid.Parse("5c000000-0000-0000-0000-000000000001");

    private static readonly MemorySeedOptions Settings = new() { TenantId = "local", Owner = "owner" };

    private static readonly MemoryEmbeddingRoute Route = new(
        LocalModelTestSupport.RouteId,
        LocalModelTestSupport.LocalProviderId,
        LocalModelTestSupport.ModelId);

    [Fact]
    public void ComposeForLocalModelRoute_ComparesOnlyTheIdPartOfTheCorpusModel()
    {
        var encoded = LocalModelTestSupport.Encoded(LocalModelTestSupport.FirstDigest);

        var snapshot = MemoryCorpusStatusReader.ComposeForLocalModelRoute(
            Settings, Route, LocalModelTestSupport.Corpus(encoded, Generation), workerErrorCode: null);

        Assert.Equal(nameof(MemoryCorpusState.Current), snapshot.State);
        Assert.False(snapshot.RebuildRequired);
        Assert.Equal(LocalModelTestSupport.ModelId, snapshot.ConfiguredModel);
        Assert.Equal(encoded, snapshot.ActiveEmbeddingModel);
        Assert.Equal(Generation, snapshot.ActiveGeneration);
    }

    [Fact]
    public void ComposeForLocalModelRoute_CorpusOfAnotherModelId_IsARouteChange()
    {
        var otherModel = LocalModelTestSupport.Encoded(LocalModelTestSupport.FirstDigest, "intfloat/multilingual-e5-base");

        var snapshot = MemoryCorpusStatusReader.ComposeForLocalModelRoute(
            Settings, Route, LocalModelTestSupport.Corpus(otherModel, Generation), workerErrorCode: null);

        Assert.Equal(nameof(MemoryCorpusState.EmbeddingRouteChanged), snapshot.State);
        Assert.True(snapshot.RebuildRequired);
    }

    [Theory]
    [InlineData(MemoryCorpusErrorCodes.EmbeddingModelMismatch, nameof(MemoryCorpusState.EmbeddingModelMismatch), false)]
    [InlineData(MemoryCorpusErrorCodes.EmbeddingModelUnavailable, nameof(MemoryCorpusState.EmbeddingModelUnavailable), false)]
    [InlineData(MemoryCorpusErrorCodes.EmbeddingRouteChanged, nameof(MemoryCorpusState.EmbeddingRouteChanged), true)]
    [InlineData("memory_sync_failed", nameof(MemoryCorpusState.Current), false)]
    public void ComposeForLocalModelRoute_FoldsTheCodeTheWorkerRecorded(string workerErrorCode, string state, bool rebuildRequired)
    {
        var encoded = LocalModelTestSupport.Encoded(LocalModelTestSupport.FirstDigest);

        var snapshot = MemoryCorpusStatusReader.ComposeForLocalModelRoute(
            Settings, Route, LocalModelTestSupport.Corpus(encoded, Generation), workerErrorCode);

        Assert.Equal(state, snapshot.State);
        Assert.Equal(rebuildRequired, snapshot.RebuildRequired);
    }

    [Fact]
    public void ComposeForLocalModelRoute_ARouteChangeCodeDoesNotOverrideAnEmptyCorpus()
    {
        var snapshot = MemoryCorpusStatusReader.ComposeForLocalModelRoute(
            Settings,
            Route,
            new MemoryCorpusInventory(null, [], 0, 0),
            MemoryCorpusErrorCodes.EmbeddingRouteChanged);

        Assert.Equal(nameof(MemoryCorpusState.NotBuilt), snapshot.State);
    }

    [Fact]
    public void ComposeForLocalModelRoute_TwoEncodedIdentitiesWithOneIdAreStillMixed()
    {
        var first = new MemoryCorpusIdentity(null, null, "local-onnx", LocalModelTestSupport.Encoded(LocalModelTestSupport.FirstDigest), 8);
        var second = first with { EmbeddingModel = LocalModelTestSupport.Encoded(LocalModelTestSupport.SecondDigest) };

        var snapshot = MemoryCorpusStatusReader.ComposeForLocalModelRoute(
            Settings, Route, new MemoryCorpusInventory(null, [first, second], 2, 2), workerErrorCode: null);

        Assert.Equal(nameof(MemoryCorpusState.MixedEmbeddingRoutes), snapshot.State);
    }

    [Fact]
    public void Compose_WithTheWorkersBlockedState_ReportsItWithoutRequiringARebuild()
    {
        var snapshot = MemoryCorpusStatusReader.Compose(
            Settings,
            Route,
            LocalModelTestSupport.Corpus(LocalModelTestSupport.Encoded(LocalModelTestSupport.FirstDigest), Generation),
            MemoryCorpusState.EmbeddingModelMismatch);

        Assert.Equal(nameof(MemoryCorpusState.EmbeddingModelMismatch), snapshot.State);
        Assert.False(snapshot.RebuildRequired);
    }
}
