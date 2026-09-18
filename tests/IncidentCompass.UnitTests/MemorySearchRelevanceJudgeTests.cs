using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Memory;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Infrastructure.Relevance.LocalOnnx;
using Microsoft.Extensions.DependencyInjection;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The relevance judge as the admission authority of <c>memory_search</c>. Every case here runs against
/// a scripted judge rather than the real cross-encoder: the thresholds, the bands, the ordering and the
/// two ways of having no judge are backend decisions, and a test that needed a 544 MiB model to state
/// them would be measuring the model instead.
/// </summary>
public sealed class MemorySearchRelevanceJudgeTests
{
    private const string FullyCoveredQuery = "checkout timeout inventory";
    private const string EnglishOffTopicQuery = "certificate rotation handshake";
    private const string PolishOffTopicQuery = "handshake przy rotacji certyfikatu";
    private const string RussianQuery = "таймаут оформления заказа";
    private const string InferenceFailedCode = "relevance_judge_inference_failed";

    /// <summary>
    /// At or above the confirm score the match is confirmed; between the floor and the confirm score it
    /// is admitted unconfirmed and banded <c>low</c> however well the words line up. A confirmed match
    /// reaches <c>high</c> only when lexical coverage of the fault query is full as well, so a band never
    /// claims more than the weaker of the two judgements says. The scripted judge gives the same score
    /// to the admission call and the confirmation call, so the score here stands for both, and the
    /// coverage that varies is the fault's, because that is what the band is decided against.
    /// </summary>
    [Theory]
    [InlineData(3.0f, MemorySearchToolTestSupport.FullyDescribingSummary, MemoryRetrievalConfidence.High)]
    [InlineData(3.0f, MemorySearchToolTestSupport.PartiallyDescribingSummary, MemoryRetrievalConfidence.Medium)]
    [InlineData(0.5f, MemorySearchToolTestSupport.FullyDescribingSummary, MemoryRetrievalConfidence.Low)]
    [InlineData(0.0f, MemorySearchToolTestSupport.FullyDescribingSummary, MemoryRetrievalConfidence.Low)]
    public async Task ExecuteAsync_TheTwoThresholdsDecideAdmissionAndTheBandFollowsBothJudgements(
        float score,
        string faultSummary,
        string expectedBand)
    {
        var output = await ExecuteAsync(
            FullyCoveredQuery,
            new ScriptedMemoryRelevanceJudge(score),
            signal: MemorySearchToolTestSupport.TriggerSignal(faultSummary));

        Assert.True(output.Result.GetProperty("matched").GetBoolean());
        Assert.Equal(expectedBand, output.Items[0].GetProperty("retrievalConfidence").GetString());
        Assert.Equal(expectedBand, output.ArtifactBands[0]);
        Assert.Equal(
            expectedBand == MemoryRetrievalConfidence.Low
                ? MemorySearchMessage.RelatedMatches
                : MemorySearchMessage.MatchesFound,
            output.Result.GetProperty("message").GetString());
    }

    /// <summary>
    /// Both thresholds are inclusive at their own edge. The scores here are exactly representable as
    /// floats, which is what the port returns: a threshold such as 1.15 is not, so a test
    /// that scored exactly it would be asserting binary rounding rather than the rule.
    /// </summary>
    [Theory]
    [InlineData(1.25f, MemoryRetrievalConfidence.High)]
    [InlineData(-1.5f, MemoryRetrievalConfidence.Low)]
    public async Task ExecuteAsync_ScoreExactlyOnAThresholdFallsOnTheAdmittingSide(float score, string expectedBand)
    {
        var output = await ExecuteAsync(
            FullyCoveredQuery,
            new ScriptedMemoryRelevanceJudge(score),
            confirmScore: 1.25,
            floorScore: -1.5,
            signal: DescribingSignal());

        Assert.Equal(expectedBand, output.Items[0].GetProperty("retrievalConfidence").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_CandidateBelowTheFloorIsDroppedRatherThanReturnedUnconfirmed()
    {
        var output = await ExecuteAsync(FullyCoveredQuery, new ScriptedMemoryRelevanceJudge(-3.0f));

        Assert.False(output.Result.GetProperty("matched").GetBoolean());
        Assert.Empty(output.Items);
        Assert.Equal(MemorySearchMessage.NoMatches, output.Result.GetProperty("message").GetString());
        Assert.Equal(MemorySearchMessage.NoMatches, output.Result.GetProperty("noMatchReason").GetString());
    }

    /// <summary>
    /// The shipped floor is a measurement, not a round number: a score just above it is admitted and a
    /// score just below it is dropped, and the two are 0.1 apart. A default that drifted would show up
    /// here rather than only in the opt-in benchmark, which needs a 544 MiB model to run at all.
    /// </summary>
    [Theory]
    [InlineData(-0.2f, 1)]
    [InlineData(-0.3f, 0)]
    public async Task ExecuteAsync_TheShippedFloorSeparatesScoresATenthApart(float score, int expectedItemCount)
    {
        var output = await ExecuteAsync(FullyCoveredQuery, new ScriptedMemoryRelevanceJudge(score));

        Assert.Equal(-0.25, MemoryRelevanceJudgeSetting.DefaultFloorScore);
        Assert.Equal(expectedItemCount, output.Items.Count);
    }

    /// <summary>
    /// The judge admits, so the vector-only fallback has nothing left to decide: even <c>always</c>
    /// cannot put a dropped candidate back. That is the whole point of the judge being the authority
    /// for every query rather than only for a query the lexical gate emptied.
    /// </summary>
    [Theory]
    [InlineData(EnglishOffTopicQuery)]
    [InlineData(PolishOffTopicQuery)]
    [InlineData(RussianQuery)]
    public async Task ExecuteAsync_OffTopicQueryInAnyLanguageReturnsNothing(string query)
    {
        var output = await ExecuteAsync(
            query,
            new ScriptedMemoryRelevanceJudge(-3.0f, -8.0f),
            [Match(1), Match(2)],
            vectorOnlyFallback: MemorySearchVectorOnlyFallbackSetting.Always);

        Assert.False(output.Result.GetProperty("matched").GetBoolean());
        Assert.Empty(output.Items);
        Assert.Equal(MemorySearchMessage.NoMatches, output.Result.GetProperty("noMatchReason").GetString());
    }

    [Fact]
    public void Rank_JudgeScoreIsTheBaseTermInPlaceOfTheVectorScore()
    {
        var lowVectorHighJudge = MemorySearchToolTestSupport.Match(2, score: 0.10);
        var highVectorLowJudge = MemorySearchToolTestSupport.Match(1, score: 0.99);

        var ranked = Rank(
            [highVectorLowJudge, lowVectorHighJudge],
            [Judged(highVectorLowJudge, 1.2), Judged(lowVectorHighJudge, 5.0)]);

        Assert.Equal(
            [lowVectorHighJudge.ChunkId, highVectorLowJudge.ChunkId],
            ranked.Select(static match => match.Match.ChunkId));
    }

    /// <summary>
    /// The documentation boost keeps its current values and therefore keeps outranking the base term,
    /// which is the existing product rule: a current runbook should beat a stale one. Here the stale
    /// chunk is the one the judge scored higher, and it still ranks second.
    /// </summary>
    [Fact]
    public void Rank_DocumentationBoostStillOutranksTheJudgeScore()
    {
        var current = MemorySearchToolTestSupport.Match(3, score: 0.10, release: "2.4.0");
        var stale = MemorySearchToolTestSupport.Match(2, score: 0.99, release: "2.3.0");

        var ranked = Rank(
            [stale, current],
            [Judged(stale, 5.0), Judged(current, 1.2)],
            currentReleases: true);

        Assert.Equal(
            [current.ChunkId, stale.ChunkId],
            ranked.Select(static match => match.Match.ChunkId));
    }

    [Fact]
    public async Task ExecuteAsync_ReportsTheVectorScoreUnchangedAndTheJudgeScoreAsItsOwnField()
    {
        var output = await ExecuteAsync(
            FullyCoveredQuery,
            new ScriptedMemoryRelevanceJudge(2.5f),
            [Match(1, score: 0.87)]);

        Assert.Equal(0.87, output.Items[0].GetProperty("score").GetDouble());
        Assert.Equal(2.5, output.Items[0].GetProperty("judgeScore").GetDouble());
        Assert.Equal(0.87, output.Artifacts[0]["score"]!.GetValue<double>());
        Assert.Equal(2.5, output.Artifacts[0]["judgeScore"]!.GetValue<double>());
    }

    /// <summary>
    /// <c>off</c> is the pre-judge admission path: the port is never called, no score is reported and
    /// the fallback decides admission again. Nothing is confirmed, because confirmation needs a judge,
    /// and the result says so in the same limitation a host with no judge reports: turning the judge
    /// off is a decision, but the role still has to be told that nothing it reads was confirmed.
    /// </summary>
    [Theory]
    [InlineData(null, 1, MemoryRetrievalConfidence.Low, MemorySearchMessage.VectorOnlyMatches)]
    [InlineData(MemorySearchVectorOnlyFallbackSetting.Always, 1, MemoryRetrievalConfidence.Low,
        MemorySearchMessage.VectorOnlyMatches)]
    [InlineData(MemorySearchVectorOnlyFallbackSetting.Off, 0, null, MemorySearchMessage.NoMatches)]
    public async Task ExecuteAsync_JudgeOffKeepsThePreJudgeAdmissionAndSaysNothingWasConfirmed(
        string? vectorOnlyFallback,
        int expectedItemCount,
        string? expectedBand,
        string expectedMessage)
    {
        var judge = new ScriptedMemoryRelevanceJudge(9.0f);

        var output = await ExecuteAsync(
            RussianQuery,
            judge,
            relevanceJudge: MemoryRelevanceJudgeSetting.Off,
            vectorOnlyFallback: vectorOnlyFallback);

        Assert.Equal(0, judge.CallCount);
        Assert.Equal(expectedItemCount, output.Items.Count);
        Assert.Equal(expectedMessage, output.Result.GetProperty("message").GetString());
        Assert.Equal(
            MemoryRelevanceJudgePass.NoJudgeLimitation,
            output.Result.GetProperty("limitation").GetString());
        if (expectedBand is not null)
        {
            Assert.Equal(expectedBand, output.Items[0].GetProperty("retrievalConfidence").GetString());
            Assert.Equal(JsonValueKind.Null, output.Items[0].GetProperty("judgeScore").ValueKind);
        }
    }

    [Fact]
    public async Task ExecuteAsync_HostThatComposesNoJudgeKeepsThePreJudgeBehaviourAndStatesTheLimitation()
    {
        AssertNotJudged(await ExecuteAsync(RussianQuery, judge: null));
    }

    /// <summary>
    /// The falling-back side of the line: the host is not running a judge at all. Only these two
    /// adapter codes are on it, and both carry the same normalized unavailable code as the failures
    /// below, which is exactly why the decision is not made on the normalized code.
    /// </summary>
    [Theory]
    [InlineData(LocalOnnxRelevanceJudgeProvider.NotConfiguredErrorCode)]
    [InlineData(LocalOnnxRelevanceJudgeProvider.ModelNotInstalledErrorCode)]
    public async Task ExecuteAsync_HostRunningNoJudgeKeepsThePreJudgeBehaviourAndStatesTheLimitation(
        string providerErrorCode)
    {
        var judge = new RefusingMemoryRelevanceJudge(
            MemoryRelevanceJudgeErrorCodes.Unavailable,
            providerErrorCode);

        var output = await ExecuteAsync(RussianQuery, judge);

        Assert.Equal(1, judge.CallCount);
        AssertNotJudged(output);
    }

    /// <summary>
    /// The propagating side: every one of these normalizes to the same
    /// <c>memory_relevance_judge_unavailable</c>, and every one of them is a judge this host meant to
    /// run and could not verify or reach. A host whose judge file is corrupt or tampered with must not
    /// quietly drop to lexical admission and then tell the model no judge is installed.
    /// </summary>
    [Theory]
    [InlineData(LocalOnnxRelevanceJudgeProvider.ModelDigestMismatchErrorCode)]
    [InlineData(LocalOnnxRelevanceJudgeProvider.ModelFileMissingErrorCode)]
    [InlineData(LocalOnnxRelevanceJudgeProvider.ModelFetchFailedErrorCode)]
    [InlineData(LocalOnnxRelevanceJudgeProvider.ModelDownloadTooLargeErrorCode)]
    [InlineData(LocalOnnxRelevanceJudgeProvider.InstallTimedOutErrorCode)]
    [InlineData(LocalOnnxRelevanceJudgeProvider.ManifestInvalidErrorCode)]
    [InlineData(LocalOnnxRelevanceJudgeProvider.StoreUnavailableErrorCode)]
    [InlineData(LocalOnnxRelevanceJudgeProvider.ModelUnusableErrorCode)]
    public async Task ExecuteAsync_UnavailableJudgeThatIsNotSimplyAbsentPropagates(string providerErrorCode)
    {
        var exception = await Assert.ThrowsAsync<MemoryRelevanceJudgeException>(
            () => ExecuteAsync(
                RussianQuery,
                new RefusingMemoryRelevanceJudge(MemoryRelevanceJudgeErrorCodes.Unavailable, providerErrorCode)));

        Assert.Equal(MemoryRelevanceJudgeErrorCodes.Unavailable, exception.ErrorCode);
        Assert.Equal(providerErrorCode, exception.ProviderErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_JudgedResultCarriesNoLimitation()
    {
        var output = await ExecuteAsync(FullyCoveredQuery, new ScriptedMemoryRelevanceJudge(3.0f));

        Assert.Equal(JsonValueKind.Null, output.Result.GetProperty("limitation").ValueKind);
    }

    /// <summary>
    /// A judge that is installed and then fails has said nothing about relevance. Answering anyway
    /// would be the tool inventing the answer it exists to stop inventing, so the failure propagates
    /// with its own code and no fallback runs.
    /// </summary>
    [Theory]
    [InlineData(InferenceFailedCode)]
    [InlineData(LocalOnnxRelevanceJudgeProvider.ModelLoadFailedErrorCode)]
    [InlineData(LocalOnnxRelevanceJudgeProvider.OutputShapeInvalidErrorCode)]
    [InlineData(MemoryRelevanceJudgeErrorCodes.Mismatch)]
    public async Task ExecuteAsync_InstalledJudgeThatFailsPropagatesItsCodeAndDoesNotFallBack(string errorCode)
    {
        var exception = await Assert.ThrowsAsync<MemoryRelevanceJudgeException>(
            () => ExecuteAsync(RussianQuery, new RefusingMemoryRelevanceJudge(errorCode, InferenceFailedCode)));

        Assert.Equal(errorCode, exception.ErrorCode);
        Assert.Equal(InferenceFailedCode, exception.ProviderErrorCode);
    }

    /// <summary>
    /// An adapter that breaks the port contract is refused by name rather than used. A NaN compares
    /// false against both thresholds, so without this it would be admitted as an unconfirmed match.
    /// </summary>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public async Task ExecuteAsync_NonFiniteScoreIsRefusedByName(float score)
    {
        var exception = await Assert.ThrowsAsync<MemoryRelevanceJudgeException>(
            () => ExecuteAsync(FullyCoveredQuery, new ScriptedMemoryRelevanceJudge(score)));

        Assert.Equal(MemoryRelevanceJudgeErrorCodes.ScoreNotFinite, exception.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_ScoreCountThatDoesNotMatchTheCandidatesIsRefusedByName()
    {
        var exception = await Assert.ThrowsAsync<MemoryRelevanceJudgeException>(
            () => ExecuteAsync(
                FullyCoveredQuery,
                new ShortScoringMemoryRelevanceJudge(3.0f),
                [Match(1), Match(2)]));

        Assert.Equal(MemoryRelevanceJudgeErrorCodes.ScoreCountMismatch, exception.ErrorCode);
    }

    private static void AssertNotJudged(ToolOutput output)
    {
        Assert.True(output.Result.GetProperty("matched").GetBoolean());
        Assert.Equal(
            MemorySearchMessage.VectorOnlyMatches,
            output.Result.GetProperty("message").GetString());
        Assert.Equal(
            MemoryRelevanceJudgePass.NoJudgeLimitation,
            output.Result.GetProperty("limitation").GetString());
        Assert.Equal(
            MemoryRetrievalConfidence.Low,
            output.Items[0].GetProperty("retrievalConfidence").GetString());
        Assert.Equal(JsonValueKind.Null, output.Items[0].GetProperty("judgeScore").ValueKind);
    }

    /// <summary>
    /// The admission call is made on every query and is always the first call. A context that carries
    /// a fault is then confirmed by a second call, which the fault-confirmation tests cover; the call
    /// count here is two for that reason, not because admission asks twice.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AsksTheJudgeAboutEveryQueryIncludingOneTheLexicalGateWouldHaveKept()
    {
        var judge = new ScriptedMemoryRelevanceJudge(3.0f);

        await ExecuteAsync(FullyCoveredQuery, judge, [Match(1), Match(2)], signal: DescribingSignal());

        Assert.Equal(2, judge.CallCount);
        var (admissionQuery, admissionCandidates) = judge.Calls[0];
        Assert.Equal(FullyCoveredQuery, admissionQuery);
        Assert.Equal(2, admissionCandidates.Count);
        Assert.All(admissionCandidates, candidate =>
            Assert.Equal(MemorySearchToolTestSupport.EnglishChunk, candidate));
    }

    [Fact]
    public async Task ExecuteAsync_ConfiguredThresholdsReplaceTheDefaults()
    {
        var confirmed = await ExecuteAsync(
            FullyCoveredQuery,
            new ScriptedMemoryRelevanceJudge(0.5f),
            confirmScore: 0.4,
            floorScore: -1.0,
            signal: DescribingSignal());
        var dropped = await ExecuteAsync(
            FullyCoveredQuery,
            new ScriptedMemoryRelevanceJudge(0.5f),
            confirmScore: 4.0,
            floorScore: 1.0);

        Assert.Equal(
            MemoryRetrievalConfidence.High,
            confirmed.Items[0].GetProperty("retrievalConfidence").GetString());
        Assert.Empty(dropped.Items);
    }

    /// <summary>
    /// The judge reaches the tool through an optional constructor parameter, so a host that composes
    /// none still resolves it. Both shapes are asserted through the container rather than by calling
    /// the constructor, because a container that stopped honouring the default would compose a tool
    /// that silently never judges.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddMemorySearchTool_ResolvesWithTheComposedJudgeAndWithoutOne(bool composeJudge)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingClient, StubEmbeddingClient>();
        services.AddSingleton<IMemoryRepository>(new StubMemoryRepository([Match(1)]));
        if (composeJudge)
        {
            services.AddSingleton<IMemoryRelevanceJudge>(new ScriptedMemoryRelevanceJudge(3.0f));
        }

        services.AddMemorySearchTool();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var tool = Assert.Single(scope.ServiceProvider.GetServices<IImmediateAgentTool>());
        var validation = tool.Validate(
            JsonSerializer.SerializeToElement(new { query = FullyCoveredQuery }));
        var result = await tool.ExecuteAsync(
            MemorySearchToolTestSupport.Context(MemorySearchToolTestSupport.Configuration()),
            validation.SanitizedArguments,
            TestContext.Current.CancellationToken);

        var item = result.Output.GetProperty("items")[0];
        Assert.Equal(
            composeJudge ? JsonValueKind.Number : JsonValueKind.Null,
            item.GetProperty("judgeScore").ValueKind);
        Assert.Equal(
            composeJudge ? JsonValueKind.Null : JsonValueKind.String,
            result.Output.GetProperty("limitation").ValueKind);
    }

    private static IReadOnlyList<MemorySearchRankedMatch> Rank(
        IReadOnlyList<MemorySearchMatch> candidates,
        IReadOnlyList<MemoryRelevanceJudgedCandidate> admitted,
        bool currentReleases = false) =>
        MemorySearchReranker.Rank(
            FullyCoveredQuery,
            MemorySearchToolTestSupport.Configuration(currentReleases: currentReleases),
            MemorySearchToolTestSupport.ServiceName,
            candidates,
            5,
            MemorySearchVectorOnlyFallback.Off,
            MemoryRelevanceJudgement.Admitting(admitted));

    private static MemoryRelevanceJudgedCandidate Judged(MemorySearchMatch match, double score) =>
        new(match, score);

    /// <summary>A fault every counted word of which the shared English chunk carries.</summary>
    private static Signal DescribingSignal() =>
        MemorySearchToolTestSupport.TriggerSignal(MemorySearchToolTestSupport.FullyDescribingSummary);

    private static MemorySearchMatch Match(int id, double score = 0.9, string? release = null) =>
        MemorySearchToolTestSupport.Match(id, score, release: release);

    private static async Task<ToolOutput> ExecuteAsync(
        string query,
        IMemoryRelevanceJudge? judge,
        IReadOnlyList<MemorySearchMatch>? candidates = null,
        string? relevanceJudge = null,
        double? confirmScore = null,
        double? floorScore = null,
        string? vectorOnlyFallback = null,
        Signal? signal = null)
    {
        var configuration = MemorySearchToolTestSupport.Configuration(
            vectorOnlyFallback,
            relevanceJudge,
            confirmScore,
            floorScore);
        var tool = new MemorySearchTool(
            new StubEmbeddingClient(),
            new StubMemoryRepository(candidates ?? [Match(1)]),
            judge);
        var validation = tool.Validate(JsonSerializer.SerializeToElement(new { query }));
        var result = await tool.ExecuteAsync(
            MemorySearchToolTestSupport.Context(configuration, signal),
            validation.SanitizedArguments,
            TestContext.Current.CancellationToken);

        return new ToolOutput(
            result.Output,
            result.Output.GetProperty("items").EnumerateArray().ToArray(),
            (result.Artifacts ?? []).Select(static draft => draft.Payload).ToArray(),
            (result.Artifacts ?? [])
                .Select(static draft => draft.Payload["retrievalConfidence"]!.GetValue<string>())
                .ToArray());
    }

    private sealed record ToolOutput(
        JsonElement Result,
        IReadOnlyList<JsonElement> Items,
        IReadOnlyList<JsonNode> Artifacts,
        IReadOnlyList<string> ArtifactBands);
}
