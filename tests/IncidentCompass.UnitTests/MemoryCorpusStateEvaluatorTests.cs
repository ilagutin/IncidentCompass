using IncidentCompass.Application.Memory;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The pre-embedding half of route-change detection: what configuration alone can decide about a
/// corpus, before any provider is asked for a vector.
/// </summary>
public sealed class MemoryCorpusStateEvaluatorTests
{
    [Fact]
    public void Evaluate_EmptyCorpus_IsNotBuilt()
    {
        var state = MemoryCorpusStateEvaluator.Evaluate(
            "local-oai", "embed-small", new MemoryCorpusInventory(null, [], 0, 0));

        Assert.Equal(MemoryCorpusState.NotBuilt, state);
    }

    [Fact]
    public void Evaluate_RecordedGenerationMatchingConfiguration_IsCurrent()
    {
        var identity = new MemoryCorpusIdentity("memory-embed", "local-oai", "openai-compatible", "embed-small", 4);

        var state = MemoryCorpusStateEvaluator.Evaluate("local-oai", "embed-small", Inventory(identity, identity));

        Assert.Equal(MemoryCorpusState.Current, state);
    }

    [Fact]
    public void Evaluate_ConfiguredModelDiffersFromCorpus_IsEmbeddingRouteChanged()
    {
        var identity = new MemoryCorpusIdentity("memory-embed", "local-oai", "openai-compatible", "embed-small", 4);

        var state = MemoryCorpusStateEvaluator.Evaluate("local-oai", "embed-large", Inventory(identity, identity));

        Assert.Equal(MemoryCorpusState.EmbeddingRouteChanged, state);
    }

    /// <summary>
    /// Every OpenAI-compatible provider reports one adapter name, so moving the route to a second
    /// embedding server leaves the model name and the vector width identical. Only the recorded
    /// provider identifier separates the two, which is why the generation records it.
    /// </summary>
    [Fact]
    public void Evaluate_SameModelOnADifferentConfiguredProvider_IsEmbeddingRouteChanged()
    {
        var identity = new MemoryCorpusIdentity("memory-embed", "local-oai", "openai-compatible", "embed-small", 4);

        var state = MemoryCorpusStateEvaluator.Evaluate("second-oai", "embed-small", Inventory(identity, identity));

        Assert.Equal(MemoryCorpusState.EmbeddingRouteChanged, state);
    }

    [Fact]
    public void Evaluate_CorpusHoldingTwoVectorSpaces_IsMixedEmbeddingRoutes()
    {
        var first = new MemoryCorpusIdentity(null, null, "openai-compatible", "embed-small", 4);
        var second = new MemoryCorpusIdentity(null, null, "openai-compatible", "embed-large", 8);

        var state = MemoryCorpusStateEvaluator.Evaluate(
            "local-oai", "embed-small", new MemoryCorpusInventory(null, [first, second], 2, 2));

        Assert.Equal(MemoryCorpusState.MixedEmbeddingRoutes, state);
    }

    [Fact]
    public void Evaluate_MatchingCorpusWithNoRecordedGeneration_IsUnrecorded()
    {
        var active = new MemoryCorpusIdentity(null, null, "openai-compatible", "embed-small", 4);

        var state = MemoryCorpusStateEvaluator.Evaluate(
            "local-oai", "embed-small", new MemoryCorpusInventory(null, [active], 1, 1));

        Assert.Equal(MemoryCorpusState.Unrecorded, state);
    }

    /// <summary>
    /// A generation left behind by a corpus that has since been replaced is not evidence about the
    /// chunks that are there now, so its recorded provider does not get to vouch for them.
    /// </summary>
    [Fact]
    public void Evaluate_RecordedGenerationDescribingADifferentSpaceThanTheChunks_IsUnrecorded()
    {
        var recorded = new MemoryCorpusIdentity("memory-embed", "local-oai", "openai-compatible", "embed-large", 8);
        var active = new MemoryCorpusIdentity(null, null, "openai-compatible", "embed-small", 4);

        var state = MemoryCorpusStateEvaluator.Evaluate(
            "local-oai", "embed-small", Inventory(recorded, active));

        Assert.Equal(MemoryCorpusState.Unrecorded, state);
    }

    /// <summary>
    /// A corpus published before provider identity was recorded is not reported as changed on that
    /// basis alone; an unrecorded provider means unknown, not different.
    /// </summary>
    [Fact]
    public void Evaluate_BackfilledGenerationWithoutAProviderIdentifier_IsCurrent()
    {
        var recorded = new MemoryCorpusIdentity(null, null, "openai-compatible", "embed-small", 4);

        var state = MemoryCorpusStateEvaluator.Evaluate("local-oai", "embed-small", Inventory(recorded, recorded));

        Assert.Equal(MemoryCorpusState.Current, state);
    }

    private static MemoryCorpusInventory Inventory(MemoryCorpusIdentity recorded, MemoryCorpusIdentity active) =>
        new(
            new MemoryCorpusGeneration(Guid.NewGuid(), recorded, 2, 2, DateTimeOffset.UnixEpoch),
            [active],
            2,
            2);
}
