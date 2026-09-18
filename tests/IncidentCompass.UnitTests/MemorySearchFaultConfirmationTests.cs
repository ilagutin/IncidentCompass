using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Memory;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Infrastructure.Relevance.LocalOnnx;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The band of every returned <c>memory_search</c> document is decided against the fault query the
/// backend builds from the trigger signal, never against the query the role wrote. Admission and order
/// stay on the role's query. Every case runs against a scripted judge that answers each call from its
/// own script, so the admission call and the confirmation call can disagree the way a real judge does
/// when the role's query and the fault are about different things.
/// </summary>
public sealed class MemorySearchFaultConfirmationTests
{
    /// <summary>
    /// A document about something the fault is not, whose whole text is the role's query. Before the
    /// band moved to the fault query, a role could re-query with this text and have it confirmed.
    /// </summary>
    private const string AttackText = "payment gateway certificate rotation broke the tls handshake";

    private const string FullyCoveredQuery = "checkout timeout inventory";
    private const string PartiallyCoveredQuery = "checkout timeout connection";
    private const string RussianQuery = "таймаут оформления заказа";
    private const string InferenceFailedCode = "relevance_judge_inference_failed";

    /// <summary>
    /// The attack, and the case this whole change exists for. The admission call scores the document
    /// highly, because the role's query is the document's own text, and every word of that query is in
    /// the document, so the band the old rule computed from those two was <c>high</c>. Against the
    /// fault it scores low, so it is still returned as related context and banded <c>low</c>, and the
    /// publication rule will refuse a <c>KnownIncident</c> that rests on it.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ADocumentWhoseTextIsTheQueryButNotTheFaultIsAdmittedAndBandedLow()
    {
        var judge = Judge(Answer(8.0f), Answer(-6.0f));
        var signal = CheckoutTimeoutSignal();

        var output = await ExecuteAsync(AttackText, judge, [Match(1, AttackText)], signal);

        Assert.True(MemorySearchLexicalFilter.Evaluate(AttackText, AttackText).IsFullyCovered);
        Assert.True(output.Result.GetProperty("matched").GetBoolean());
        var item = Assert.Single(output.Items);
        Assert.Equal(MemoryRetrievalConfidence.Low, item.GetProperty("retrievalConfidence").GetString());
        Assert.Equal(MemoryRetrievalConfidence.Low, output.Artifacts[0]["retrievalConfidence"]!.GetValue<string>());
        Assert.Equal(8.0, item.GetProperty("judgeScore").GetDouble());
        Assert.Equal(-6.0, item.GetProperty("confirmationScore").GetDouble());
        Assert.Equal(MemorySearchMessage.RelatedMatches, output.Result.GetProperty("message").GetString());
        Assert.Equal(MemoryFaultQuery.For(signal), judge.Calls[1].Query);
    }

    /// <summary>
    /// The same attack on a host with no judge: the role's query admits the document lexically, the
    /// fault query does not support it, and the band is <c>low</c>.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithoutAJudgeTheAttackIsAdmittedLexicallyAndBandedLow()
    {
        var output = await ExecuteAsync(AttackText, judge: null, [Match(1, AttackText)], CheckoutTimeoutSignal());

        var item = Assert.Single(output.Items);
        Assert.Equal(MemoryRetrievalConfidence.Low, item.GetProperty("retrievalConfidence").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("confirmationScore").ValueKind);
        Assert.Equal(MemorySearchMessage.RelatedMatches, output.Result.GetProperty("message").GetString());
        Assert.Equal(
            MemoryRelevanceJudgePass.NoJudgeLimitation,
            output.Result.GetProperty("limitation").GetString());
    }

    /// <summary>
    /// The same document, under a fault that does describe it, is confirmed: the change moves the
    /// question the band answers, it does not stop confirming.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TheSameDocumentIsMediumWhenTheFaultDescribesIt()
    {
        var signal = MemorySearchToolTestSupport.TriggerSignal(
            "payment gateway certificate handshake failed",
            serviceName: "payments-api");

        var output = await ExecuteAsync(AttackText, Judge(Answer(8.0f), Answer(3.0f)), [Match(1, AttackText)], signal);

        Assert.Equal(MemoryRetrievalConfidence.Medium, output.Items[0].GetProperty("retrievalConfidence").GetString());
        Assert.Equal(MemorySearchMessage.MatchesFound, output.Result.GetProperty("message").GetString());
    }

    /// <summary>
    /// <c>high</c> needs every counted word of the fault query in the chunk. Full coverage of the role's
    /// query is not enough, and partial coverage of it is no obstacle.
    /// </summary>
    [Theory]
    [InlineData(FullyCoveredQuery, MemorySearchToolTestSupport.PartiallyDescribingSummary, MemoryRetrievalConfidence.Medium)]
    [InlineData(PartiallyCoveredQuery, MemorySearchToolTestSupport.FullyDescribingSummary, MemoryRetrievalConfidence.High)]
    public async Task ExecuteAsync_HighNeedsFullCoverageOfTheFaultQueryNotOfTheRoleQuery(
        string query,
        string faultSummary,
        string expectedBand)
    {
        var output = await ExecuteAsync(
            query,
            Judge(Answer(3.0f), Answer(3.0f)),
            [Match(1)],
            MemorySearchToolTestSupport.TriggerSignal(faultSummary));

        Assert.Equal(expectedBand, output.Items[0].GetProperty("retrievalConfidence").GetString());
    }

    /// <summary>
    /// Two calls when a judge exists: the first on the role's query and every candidate, the second on
    /// the fault query and only the candidates that are being returned, in their returned order. Here
    /// one candidate is below the floor and one is past <c>TopK</c>, and the second call sees neither.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TheSecondCallScoresOnlyTheReturnedCandidatesAgainstTheFaultQuery()
    {
        MemorySearchMatch[] candidates =
        [
            Match(1, MemorySearchToolTestSupport.EnglishChunk),
            Match(2, "dropped below the floor"),
            Match(3, "checkout timeout runbook alpha"),
            Match(4, "checkout timeout runbook beta")
        ];
        var judge = Judge(Answer(3.0f, -9.0f, 2.0f, 1.0f), Answer(3.0f, 3.0f));
        var signal = CheckoutTimeoutSignal();

        var output = await ExecuteAsync(FullyCoveredQuery, judge, candidates, signal, topK: 2);

        Assert.Equal(2, judge.CallCount);
        Assert.Equal(FullyCoveredQuery, judge.Calls[0].Query);
        Assert.Equal(candidates.Select(static candidate => candidate.Text), judge.Calls[0].Candidates);
        Assert.Equal(MemoryFaultQuery.For(signal), judge.Calls[1].Query);
        Assert.Equal(new[] { candidates[0].Text, candidates[2].Text }, judge.Calls[1].Candidates);
        Assert.Equal(2, output.Items.Count);
    }

    /// <summary>
    /// No judge, nothing confirmed. Retrieval and admission still run, lexically or through the
    /// vector-only fallback, and the items are still returned as context, but every one of them is
    /// <c>low</c> whatever the fault says, and the result says why. That includes a fault the chunk
    /// covers word for word, and a Cyrillic fault whose only eligible word is the service name, which is
    /// exactly the case a lexical confirmation would get wrong.
    /// </summary>
    [Theory]
    [InlineData(FullyCoveredQuery, MemorySearchToolTestSupport.FullyDescribingSummary, MemorySearchMessage.RelatedMatches)]
    [InlineData(FullyCoveredQuery, MemorySearchToolTestSupport.PartiallyDescribingSummary, MemorySearchMessage.RelatedMatches)]
    [InlineData(FullyCoveredQuery, MemorySearchToolTestSupport.UnrelatedSummary, MemorySearchMessage.RelatedMatches)]
    [InlineData(FullyCoveredQuery, "оформление заказа зависло", MemorySearchMessage.RelatedMatches)]
    [InlineData(RussianQuery, MemorySearchToolTestSupport.FullyDescribingSummary, MemorySearchMessage.VectorOnlyMatches)]
    public async Task ExecuteAsync_WithoutAJudgeNothingIsConfirmed(
        string query,
        string faultSummary,
        string expectedMessage)
    {
        var output = await ExecuteAsync(
            query,
            judge: null,
            [Match(1)],
            MemorySearchToolTestSupport.TriggerSignal(faultSummary));

        var item = Assert.Single(output.Items);
        Assert.Equal(MemoryRetrievalConfidence.Low, item.GetProperty("retrievalConfidence").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("confirmationScore").ValueKind);
        Assert.Equal(expectedMessage, output.Result.GetProperty("message").GetString());
        Assert.Equal(
            MemoryRelevanceJudgePass.NoJudgeLimitation,
            output.Result.GetProperty("limitation").GetString());
    }

    /// <summary>
    /// Nothing to confirm against confirms nothing. A context with no signal, and a signal whose every
    /// word is a stop word or too short to count, band every item <c>low</c> on both paths, and the
    /// judge is not asked a second time. The lexical gate treats a query with no counted word as
    /// supported; that vacuous rule is an admission rule and must not reach the band.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ExecuteAsync_NoSignalOrAWordlessFaultBandsEverythingLow(bool withSignal, bool withJudge)
    {
        var signal = withSignal
            ? MemorySearchToolTestSupport.TriggerSignal("the error in the service", errorMessage: "it is on", serviceName: "api")
            : null;
        var judge = withJudge ? Judge(Answer(3.0f), Answer(3.0f)) : null;

        var output = await ExecuteAsync(FullyCoveredQuery, judge, [Match(1), Match(2)], signal);

        Assert.Equal(2, output.Items.Count);
        Assert.All(output.Items, item =>
        {
            Assert.Equal(MemoryRetrievalConfidence.Low, item.GetProperty("retrievalConfidence").GetString());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("confirmationScore").ValueKind);
        });
        Assert.Equal(MemorySearchMessage.RelatedMatches, output.Result.GetProperty("message").GetString());
        Assert.Equal(withJudge ? 1 : 0, judge?.CallCount ?? 0);
        if (signal is not null)
        {
            Assert.False(MemorySearchLexicalFilter.HasCountedWord(MemoryFaultQuery.For(signal)!));
        }
    }

    [Fact]
    public void JudgedBand_AFaultQueryWithNoCountedWordConfirmsNothing()
    {
        var support = MemorySearchLexicalFilter.Evaluate("the api error", MemorySearchToolTestSupport.EnglishChunk);

        Assert.True(support.IsSupported);
        Assert.Equal(MemoryRetrievalConfidence.Low, MemoryRetrievalConfidence.JudgedBand(confirmed: true, support));
    }

    /// <summary>
    /// A judge that answered the admission call and then fails the confirmation call has not confirmed
    /// anything, and is treated exactly as a failed admission call is: the failure propagates with its
    /// own code, and no band is produced from nothing.
    /// </summary>
    [Theory]
    [InlineData(InferenceFailedCode)]
    [InlineData(LocalOnnxRelevanceJudgeProvider.ModelLoadFailedErrorCode)]
    public async Task ExecuteAsync_ASecondCallFailurePropagatesItsCode(string errorCode)
    {
        var judge = Judge(Answer(3.0f), Refuse(errorCode, InferenceFailedCode));

        var exception = await Assert.ThrowsAsync<MemoryRelevanceJudgeException>(
            () => ExecuteAsync(FullyCoveredQuery, judge, [Match(1)], CheckoutTimeoutSignal()));

        Assert.Equal(errorCode, exception.ErrorCode);
        Assert.Equal(2, judge.CallCount);
    }

    /// <summary>
    /// The two absence codes are a host with no judge, on the second call as on the first, and a call
    /// no judge confirmed confirms nothing: every item is <c>low</c> and the result says so, even for a
    /// fault the chunk covers word for word. There is no lexical fallback. The admission score stays,
    /// because the admission call did run.
    /// </summary>
    [Theory]
    [InlineData(LocalOnnxRelevanceJudgeProvider.NotConfiguredErrorCode)]
    [InlineData(LocalOnnxRelevanceJudgeProvider.ModelNotInstalledErrorCode)]
    public async Task ExecuteAsync_ASecondCallAbsenceConfirmsNothingAndSaysSo(string providerErrorCode)
    {
        var judge = Judge(Answer(3.0f), Refuse(MemoryRelevanceJudgeErrorCodes.Unavailable, providerErrorCode));

        var output = await ExecuteAsync(
            FullyCoveredQuery,
            judge,
            [Match(1)],
            MemorySearchToolTestSupport.TriggerSignal(MemorySearchToolTestSupport.FullyDescribingSummary));

        var item = Assert.Single(output.Items);
        Assert.Equal(MemoryRetrievalConfidence.Low, item.GetProperty("retrievalConfidence").GetString());
        Assert.Equal(3.0, item.GetProperty("judgeScore").GetDouble());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("confirmationScore").ValueKind);
        Assert.Equal(MemorySearchMessage.RelatedMatches, output.Result.GetProperty("message").GetString());
        Assert.Equal(
            MemoryFaultConfirmation.JudgeAbsentAtConfirmationLimitation,
            output.Result.GetProperty("limitation").GetString());
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public async Task ExecuteAsync_ASecondCallNonFiniteScoreIsRefusedByName(float score)
    {
        var exception = await Assert.ThrowsAsync<MemoryRelevanceJudgeException>(
            () => ExecuteAsync(
                FullyCoveredQuery,
                Judge(Answer(3.0f), Answer(score)),
                [Match(1)],
                CheckoutTimeoutSignal()));

        Assert.Equal(MemoryRelevanceJudgeErrorCodes.ScoreNotFinite, exception.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_ASecondCallWithTheWrongScoreCountIsRefusedByName()
    {
        var exception = await Assert.ThrowsAsync<MemoryRelevanceJudgeException>(
            () => ExecuteAsync(
                FullyCoveredQuery,
                Judge(Answer(3.0f, 3.0f), (_, _) => [3.0f]),
                [Match(1), Match(2)],
                CheckoutTimeoutSignal()));

        Assert.Equal(MemoryRelevanceJudgeErrorCodes.ScoreCountMismatch, exception.ErrorCode);
    }

    /// <summary>
    /// <c>confirmationScore</c> is written beside <c>score</c> and <c>judgeScore</c> in both the tool
    /// result and the durable artifact payload, rounded the same way, and is null when no judge judged.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReportsTheConfirmationScoreInTheOutputAndThePayload()
    {
        var judged = await ExecuteAsync(
            FullyCoveredQuery,
            Judge(Answer(2.5f), Answer(1.2345678f)),
            [Match(1)],
            CheckoutTimeoutSignal());
        var unjudged = await ExecuteAsync(FullyCoveredQuery, judge: null, [Match(1)], CheckoutTimeoutSignal());

        var rounded = Math.Round((double)1.2345678f, 6);
        Assert.Equal(rounded, judged.Items[0].GetProperty("confirmationScore").GetDouble());
        Assert.Equal(rounded, judged.Artifacts[0]["confirmationScore"]!.GetValue<double>());
        Assert.Equal(JsonValueKind.Null, unjudged.Items[0].GetProperty("confirmationScore").ValueKind);
        Assert.True(unjudged.Artifacts[0].AsObject().ContainsKey("confirmationScore"));
        Assert.Null(unjudged.Artifacts[0]["confirmationScore"]);
    }

    /// <summary>
    /// A checkout timeout fault, summarized the way intake summarizes one. The tests that use it assert
    /// what the judge was asked and what it answered, not the band this fault earns lexically.
    /// </summary>
    private static Signal CheckoutTimeoutSignal() =>
        MemorySearchToolTestSupport.TriggerSignal(
            "checkout-api: POST /checkout failed",
            "TimeoutException",
            "Checkout timed out while waiting on inventory");

    private static MemorySearchMatch Match(int id, string text = MemorySearchToolTestSupport.EnglishChunk) =>
        MemorySearchToolTestSupport.Match(id, text: text);

    private static SequencedMemoryRelevanceJudge Judge(
        params Func<string, IReadOnlyList<string>, IReadOnlyList<float>>[] answers) =>
        new(answers);

    private static Func<string, IReadOnlyList<string>, IReadOnlyList<float>> Answer(params float[] scores) =>
        (_, candidates) => candidates.Select((_, index) => scores[Math.Min(index, scores.Length - 1)]).ToArray();

    private static Func<string, IReadOnlyList<string>, IReadOnlyList<float>> Refuse(
        string errorCode,
        string providerErrorCode) =>
        (_, _) => throw new MemoryRelevanceJudgeException(
            "local-onnx",
            "The relevance judge refused the call.",
            errorCode: errorCode,
            providerErrorCode: providerErrorCode,
            failureKind: ProviderFailureKind.Unavailable);

    private static async Task<ToolOutput> ExecuteAsync(
        string query,
        IMemoryRelevanceJudge? judge,
        IReadOnlyList<MemorySearchMatch> candidates,
        Signal? signal,
        int? topK = null)
    {
        var tool = new MemorySearchTool(new StubEmbeddingClient(), new StubMemoryRepository(candidates), judge);
        var validation = tool.Validate(JsonSerializer.SerializeToElement(new { query }));
        var result = await tool.ExecuteAsync(
            MemorySearchToolTestSupport.Context(MemorySearchToolTestSupport.Configuration(topK: topK), signal),
            validation.SanitizedArguments,
            TestContext.Current.CancellationToken);

        return new ToolOutput(
            result.Output,
            result.Output.GetProperty("items").EnumerateArray().ToArray(),
            (result.Artifacts ?? []).Select(static draft => draft.Payload).ToArray());
    }

    private sealed record ToolOutput(
        JsonElement Result,
        IReadOnlyList<JsonElement> Items,
        IReadOnlyList<JsonNode> Artifacts);

    /// <summary>
    /// A judge that answers each call from its own script, in call order, and records every call. The
    /// admission call is always the first and the confirmation call the second.
    /// </summary>
    private sealed class SequencedMemoryRelevanceJudge(
        IReadOnlyList<Func<string, IReadOnlyList<string>, IReadOnlyList<float>>> answers) : IMemoryRelevanceJudge
    {
        private readonly List<(string Query, IReadOnlyList<string> Candidates)> calls = [];

        public int CallCount => calls.Count;

        public (string Query, IReadOnlyList<string> Candidates)[] Calls => [.. calls];

        public Task<IReadOnlyList<float>> ScoreAsync(
            string query,
            IReadOnlyList<string> candidates,
            CancellationToken cancellationToken)
        {
            calls.Add((query, candidates.ToArray()));
            return Task.FromResult(answers[calls.Count - 1](query, candidates));
        }
    }
}
