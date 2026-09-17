using System.Text.Json;
using IncidentCompass.Application.Memory;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The vector-only fallback and the retrieval-confidence bands it produces. The fallback only ever runs
/// when lexical coverage left nothing; <c>foreign_script</c> additionally requires the query to carry a
/// word in a writing system none of the candidates use, so an English query that simply matches nothing
/// still returns the honest empty result.
/// </summary>
public sealed class MemorySearchVectorOnlyFallbackTests
{
    private const string EnglishOffTopicQuery = "certificate rotation handshake";
    private const string PolishQuery = "handshake przy rotacji certyfikatu";
    private const string RussianQuery = "таймаут оформления заказа";
    private const string RussianMixedQuery = "circuit breaker при задержках склада";
    private const string UnrelatedChunk = "connection pool exhaustion runbook";

    [Theory]
    [InlineData(EnglishOffTopicQuery, "off", 0)]
    [InlineData(EnglishOffTopicQuery, "foreign_script", 0)]
    [InlineData(EnglishOffTopicQuery, "always", 2)]
    [InlineData(PolishQuery, "off", 0)]
    [InlineData(PolishQuery, "foreign_script", 0)]
    [InlineData(PolishQuery, "always", 2)]
    [InlineData(RussianQuery, "off", 0)]
    [InlineData(RussianQuery, "foreign_script", 2)]
    [InlineData(RussianQuery, "always", 2)]
    [InlineData(RussianMixedQuery, "off", 0)]
    [InlineData(RussianMixedQuery, "foreign_script", 2)]
    [InlineData(RussianMixedQuery, "always", 2)]
    public void Rank_FallbackModeDecidesWhetherUnsupportedCandidatesAreReturned(
        string query,
        string configuredFallback,
        int expectedCount)
    {
        var ranked = Rank(
            query,
            MemorySearchVectorOnlyFallbackSetting.Resolve(configuredFallback),
            [Match(1, text: UnrelatedChunk), Match(2, text: UnrelatedChunk)]);

        Assert.Equal(expectedCount, ranked.Count);
        Assert.All(ranked, match => Assert.True(match.VectorOnly));
        Assert.All(ranked, match => Assert.Equal(MemoryRetrievalConfidence.Low, match.RetrievalConfidence));
    }

    [Fact]
    public void Rank_MixedScriptQueryIsLexicallySupportedByItsLatinWordsAndBandsMedium()
    {
        var ranked = Rank(RussianMixedQuery, MemorySearchVectorOnlyFallback.ForeignScript, [Match(1)]);

        var match = Assert.Single(ranked);
        Assert.False(match.VectorOnly);
        Assert.Equal(MemoryRetrievalConfidence.Medium, match.RetrievalConfidence);
    }

    [Fact]
    public void Rank_LexicallySupportedCandidateSuppressesTheFallbackForEveryOtherCandidate()
    {
        var supported = Match(1, 0.5, MemorySearchToolTestSupport.EnglishChunk);
        var unsupported = Match(2, 0.99, "connection pool exhaustion runbook");

        var ranked = Rank("circuit breaker при задержках склада", MemorySearchVectorOnlyFallback.Always, [unsupported, supported]);

        var match = Assert.Single(ranked);
        Assert.Equal(supported.ChunkId, match.Match.ChunkId);
        Assert.False(match.VectorOnly);
    }

    [Fact]
    public void Rank_FallbackKeepsTheOrdinaryOrderingAndTopK()
    {
        var ranked = Rank(
            RussianQuery,
            MemorySearchVectorOnlyFallback.Always,
            [Match(3, 0.90), Match(1, 0.90), Match(2, 0.95)],
            topK: 2);

        Assert.Equal(
            [Match(2).ChunkId, Match(1).ChunkId],
            ranked.Select(static match => match.Match.ChunkId));
    }

    [Fact]
    public void Rank_FallbackWithNoCandidatesStillReturnsNothing()
    {
        Assert.Empty(Rank(RussianQuery, MemorySearchVectorOnlyFallback.Always, []));
    }

    /// <summary>
    /// Every script is missing from an empty set, so a candidate set with no counted word at all would
    /// otherwise make the default fire for a same-script query. That comparison is vacuous, not a
    /// foreign script, and only <c>always</c> should return those candidates.
    /// </summary>
    [Fact]
    public void Rank_ForeignScriptDoesNotFireWhenNoCandidateHasACountedWord()
    {
        MemorySearchMatch[] wordless = [Match(1, text: "a to of it"), Match(2, text: "in on by")];

        Assert.Empty(Rank(EnglishOffTopicQuery, MemorySearchVectorOnlyFallback.ForeignScript, wordless));
        Assert.Empty(Rank(PolishQuery, MemorySearchVectorOnlyFallback.ForeignScript, wordless));
        Assert.Empty(Rank(RussianQuery, MemorySearchVectorOnlyFallback.ForeignScript, wordless));
        Assert.Equal(2, Rank(EnglishOffTopicQuery, MemorySearchVectorOnlyFallback.Always, wordless).Count);
    }

    [Fact]
    public void Rank_HighBandNeedsEveryCountedQueryWordEligibleAndPresent()
    {
        var ranked = Rank("checkout timeout inventory", MemorySearchVectorOnlyFallback.Off, [Match(1)]);

        Assert.Equal(MemoryRetrievalConfidence.High, Assert.Single(ranked).RetrievalConfidence);
    }

    [Fact]
    public async Task ExecuteAsync_LexicalResultReportsMatchesFoundAndTheHighBand()
    {
        var output = await ExecuteAsync("checkout timeout inventory", vectorOnlyFallback: null);

        Assert.True(output.Result.GetProperty("matched").GetBoolean());
        Assert.Equal("matches found", output.Result.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, output.Result.GetProperty("noMatchReason").ValueKind);
        Assert.Equal(
            MemoryRetrievalConfidence.High,
            output.Result.GetProperty("items")[0].GetProperty("retrievalConfidence").GetString());
        Assert.Equal(MemoryRetrievalConfidence.High, output.ArtifactBand);
    }

    [Fact]
    public async Task ExecuteAsync_FallbackResultReportsItsOwnMessageAndTheLowBand()
    {
        var output = await ExecuteAsync(RussianQuery, vectorOnlyFallback: null);

        Assert.True(output.Result.GetProperty("matched").GetBoolean());
        Assert.Equal("vector-only matches, not lexically confirmed", output.Result.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, output.Result.GetProperty("noMatchReason").ValueKind);
        Assert.Equal(
            MemoryRetrievalConfidence.Low,
            output.Result.GetProperty("items")[0].GetProperty("retrievalConfidence").GetString());
        Assert.Equal(MemoryRetrievalConfidence.Low, output.ArtifactBand);
    }

    [Fact]
    public async Task ExecuteAsync_FallbackOffKeepsTheHonestEmptyResult()
    {
        var output = await ExecuteAsync(RussianQuery, MemorySearchVectorOnlyFallbackSetting.Off);

        Assert.False(output.Result.GetProperty("matched").GetBoolean());
        Assert.Equal("no matches", output.Result.GetProperty("message").GetString());
        Assert.Equal("no matches", output.Result.GetProperty("noMatchReason").GetString());
        Assert.Empty(output.Result.GetProperty("items").EnumerateArray());
        Assert.Null(output.ArtifactBand);
    }

    [Fact]
    public async Task ExecuteAsync_ConfiguredAlwaysReturnsUnconfirmedItemsForAnEnglishNoMatch()
    {
        var output = await ExecuteAsync(EnglishOffTopicQuery, MemorySearchVectorOnlyFallbackSetting.Always);

        Assert.Equal("vector-only matches, not lexically confirmed", output.Result.GetProperty("message").GetString());
        Assert.Equal(MemoryRetrievalConfidence.Low, output.ArtifactBand);
    }

    private static async Task<ToolOutput> ExecuteAsync(string query, string? vectorOnlyFallback)
    {
        var configuration = MemorySearchToolTestSupport.Configuration(vectorOnlyFallback);
        var tool = new MemorySearchTool(new StubEmbeddingClient(), new StubMemoryRepository([Match(1)]));
        var validation = tool.Validate(JsonSerializer.SerializeToElement(new { query }));
        var result = await tool.ExecuteAsync(
            MemorySearchToolTestSupport.Context(configuration),
            validation.SanitizedArguments,
            TestContext.Current.CancellationToken);

        return new ToolOutput(
            result.Output,
            result.Artifacts?
                .Select(static draft => draft.Payload["retrievalConfidence"]!.GetValue<string>())
                .FirstOrDefault());
    }

    private static IReadOnlyList<MemorySearchRankedMatch> Rank(
        string query,
        MemorySearchVectorOnlyFallback fallback,
        IReadOnlyList<MemorySearchMatch> candidates,
        int topK = 5) =>
        MemorySearchReranker.Rank(
            query,
            MemorySearchToolTestSupport.Configuration(),
            MemorySearchToolTestSupport.ServiceName,
            candidates,
            topK,
            fallback);

    private static MemorySearchMatch Match(
        int id,
        double score = 0.9,
        string text = MemorySearchToolTestSupport.EnglishChunk) =>
        MemorySearchToolTestSupport.Match(id, score, text);

    private sealed record ToolOutput(JsonElement Result, string? ArtifactBand);
}
