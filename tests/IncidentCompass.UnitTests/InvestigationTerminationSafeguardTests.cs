using System.Text.Json;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Application.Core.Resilience;
using IncidentCompass.Application.Core.Text;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Reports;
using static IncidentCompass.UnitTests.InvestigationProgressTestHarness;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The edges of recovery and termination: which recovery failures end the attempt honestly and which
/// go back to the job runner, what a model cannot forge, and how a stall that meets the turn or worker
/// limit, or leaves no room for another window, still ends with the backend report.
/// </summary>
public sealed class InvestigationTerminationSafeguardTests
{
    [Fact]
    public async Task ProcessAsync_RecoveryCallMeetsAProviderOutage_PropagatesForTheRunnerInsteadOfTerminating()
    {
        var model = StallingModel((_, _) => throw new AiModelException(
            "test-provider", "Provider down.", errorCode: "provider_unavailable", failureKind: ProviderFailureKind.Unavailable));
        var harness = new InvestigationProgressTestHarness(model, _ => "{}", maxTurnsWithoutProgress: 2);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => harness.ProcessAsync());

        Assert.Equal(ProviderFailureKind.Unavailable, ProviderOutageExceptionClassifier.FindFailureKind(exception));
        Assert.Null(TriageNonRetryableFailureClassifier.TryGetErrorCode(exception));
        Assert.Empty(harness.Reports.Published);
        Assert.Empty(harness.NoProgressEvents("terminated"));
        Assert.Empty(harness.NoProgressEvents("recovery_failed"));
    }

    [Theory]
    [InlineData(ProviderFailureKind.RejectedRequest)]
    [InlineData(ProviderFailureKind.OutputLimitReached)]
    [InlineData(ProviderFailureKind.InvalidResponse)]
    public async Task ProcessAsync_RecoveryCallFailsDeterministically_TerminatesWithTheBackendReport(ProviderFailureKind kind)
    {
        var model = StallingModel((_, _) => throw new AiModelException(
            "test-provider", "Refused.", errorCode: "provider_request_rejected", failureKind: kind));
        var harness = new InvestigationProgressTestHarness(model, _ => "{}", maxTurnsWithoutProgress: 2);

        await harness.ProcessAsync();

        Assert.Single(harness.NoProgressEvents("recovery_failed"));
        Assert.StartsWith("no_progress: terminated reason=no_recovery_left ", Assert.Single(harness.NoProgressEvents("terminated")).Rationale);
        Assert.True(Assert.Single(harness.Reports.Published).BackendAuthored);
    }

    [Fact]
    public async Task ProcessAsync_FailedRecoveryWithAllowanceLeft_ContinuesAndTheNextRecoveryRuns()
    {
        var model = StallingModel((call, _) => call == 1
            ? throw new AiModelException("test-provider", "Refused.", errorCode: "provider_request_rejected", failureKind: ProviderFailureKind.RejectedRequest)
            : Response("Delegate to a different role."));
        var harness = new InvestigationProgressTestHarness(model, _ => "{}", maxTurnsWithoutProgress: 2, maxRecoveries: 2);

        await harness.ProcessAsync();

        // Stall one: recovery 1 fails, a window still fits, the attempt goes on. Stall two: recovery 2
        // returns a suggestion. Stall three: nothing left, the backend report ends it.
        Assert.Equal(2, model.RecoveryCalls);
        Assert.Equal(10, model.OrchestratorCalls);
        Assert.Single(harness.NoProgressEvents("recovery_failed"));
        Assert.Single(
            model.OrchestratorRequests[^1].Messages,
            message => message.Role == AiMessageRole.User &&
                message.Content == InvestigationNoProgressHandler.RecoveryReturnedNothingMessage);
        Assert.Equal("no_progress: recovery recovery=2/2 evidence=2 suggestion_chars=29", Assert.Single(harness.NoProgressEvents("recovery ")).Rationale);
        Assert.StartsWith("no_progress: terminated reason=no_recovery_left recoveries_used=2 ", Assert.Single(harness.NoProgressEvents("terminated")).Rationale);
    }

    [Fact]
    public async Task ProcessAsync_RecoveryRefusedByTheTokenBudget_TerminatesWithoutDispatching()
    {
        InvestigationProgressTestHarness? harness = null;
        var model = new ScriptedInvestigationModel(
            (call, _) => DelegateTurn("prober", "angle " + call),
            (call, request) =>
            {
                // The answer that closes the fourth turn uses up the token budget.
                if (call == 8)
                {
                    harness!.Reader.ExtraTokensSpent = 1_000_000;
                }

                return ProbeOnceThenAnswer(call, request);
            });
        harness = new InvestigationProgressTestHarness(model, _ => "{}", maxTurnsWithoutProgress: 2);

        await harness.ProcessAsync();

        Assert.Equal(0, model.RecoveryCalls);
        Assert.Equal(
            "no_progress: recovery_failed recovery=1/1 error_code=triage_budget_max_tokens_reached",
            Assert.Single(harness.NoProgressEvents("recovery_failed")).Rationale);
        Assert.StartsWith("no_progress: terminated reason=recovery_not_admitted ", Assert.Single(harness.NoProgressEvents("terminated")).Rationale);
    }

    [Fact]
    public async Task ProcessAsync_AttemptCeilingReachedBeforeTheRecoveryCall_Propagates()
    {
        InvestigationProgressTestHarness? harness = null;
        var model = new ScriptedInvestigationModel(
            (call, _) => DelegateTurn("prober", "angle " + call),
            (call, request) =>
            {
                if (call == 8)
                {
                    harness!.Time.Advance(TimeSpan.FromHours(5));
                }

                return ProbeOnceThenAnswer(call, request);
            });
        harness = new InvestigationProgressTestHarness(model, _ => "{}", maxTurnsWithoutProgress: 2);

        var exception = await Assert.ThrowsAsync<TriageBudgetExhaustedException>(() => harness.ProcessAsync());

        Assert.Equal(TriageBudgetExhaustedException.WallClockReachedBeforeCallCode, exception.ErrorCode);
        Assert.Empty(harness.Reports.Published);
    }

    [Fact]
    public async Task ProcessAsync_CancelledDuringTheRecoveryCall_PropagatesCancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var model = StallingModel((_, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        var harness = new InvestigationProgressTestHarness(model, _ => "{}", maxTurnsWithoutProgress: 2);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.ProcessUntilCancelledAsync(cancellation.Token));

        Assert.Empty(harness.Reports.Published);
    }

    [Fact]
    public async Task ProcessAsync_BackendReportRefusedAtPublication_DeadLettersUnderItsOwnCodeWithoutATerminatedRow()
    {
        var harness = new InvestigationProgressTestHarness(StallingModel(), _ => "{}", maxTurnsWithoutProgress: 2, maxRecoveries: 0);
        harness.Reports.RefuseBackendAuthored = true;

        var exception = await Assert.ThrowsAsync<TriageBudgetExhaustedException>(() => harness.ProcessAsync());

        Assert.Equal(TriageBudgetExhaustedException.NoProgressTerminationFailedCode, exception.ErrorCode);
        Assert.Equal(
            TriageBudgetExhaustedException.NoProgressTerminationFailedCode,
            TriageNonRetryableFailureClassifier.TryGetErrorCode(exception));
        Assert.Empty(harness.NoProgressEvents("terminated"));
        Assert.Contains(harness.ProcessorLogger.Entries, entry => entry.EventId.Id == 3410);
    }

    [Fact]
    public async Task ProcessAsync_OneUnproductiveTurnAfterProgressThenTheTurnLimit_StillDeadLetters()
    {
        // Turn 1 adds evidence, turns 2 and 3 add none, and no window was ever exceeded: that is not a
        // detected stall, so the turn limit dead-letters as before.
        var harness = new InvestigationProgressTestHarness(StallingModel(), _ => "{}", maxTurnsWithoutProgress: 10, maxTurns: 2);

        var exception = await Assert.ThrowsAsync<TriageBudgetExhaustedException>(() => harness.ProcessAsync());

        Assert.Equal(TriageBudgetExhaustedException.OrchestratorTurnLimitReachedCode, exception.ErrorCode);
        Assert.Empty(harness.Reports.Published);
        Assert.Empty(harness.NoProgressEvents("terminated"));
    }

    [Fact]
    public async Task ProcessAsync_TurnLimitReachedWhileMakingProgress_StillDeadLetters()
    {
        var model = new ScriptedInvestigationModel(
            (call, _) => DelegateTurn("analysis", "angle " + call),
            (call, _) => Response(WorkerOutput("Fact " + call + ".")));
        var harness = new InvestigationProgressTestHarness(model, _ => "{}", maxTurnsWithoutProgress: 10, maxTurns: 5);

        var exception = await Assert.ThrowsAsync<TriageBudgetExhaustedException>(() => harness.ProcessAsync());

        Assert.Equal(TriageBudgetExhaustedException.OrchestratorTurnLimitReachedCode, exception.ErrorCode);
        Assert.Empty(harness.Reports.Published);
    }

    [Fact]
    public async Task ProcessAsync_OneUnproductiveTurnAfterProgressThenTheWorkerBudget_StillDeadLetters()
    {
        // Worker 1 adds evidence, worker 2 adds none, the third delegate meets MaxWorkers 2 before any
        // window was exceeded.
        var harness = new InvestigationProgressTestHarness(StallingModel(), _ => "{}", maxTurnsWithoutProgress: 10, maxWorkers: 2);

        var exception = await Assert.ThrowsAsync<TriageBudgetExhaustedException>(() => harness.ProcessAsync());

        Assert.Equal(TriageBudgetExhaustedException.MaxWorkersReachedCode, exception.ErrorCode);
        Assert.Empty(harness.Reports.Published);
    }

    [Fact]
    public async Task ProcessAsync_WorkerBudgetReachedDuringADetectedStall_PublishesTheMatchingReport()
    {
        // The stall is detected at turn 4 with one worker left, the recovery runs, the fifth delegate
        // uses the last worker and the sixth meets MaxWorkers while the stall is still open.
        var model = StallingModel();
        var harness = new InvestigationProgressTestHarness(model, _ => "{}", maxTurnsWithoutProgress: 2, maxWorkers: 5);

        await harness.ProcessAsync();

        Assert.Equal(1, model.RecoveryCalls);
        Assert.StartsWith("no_progress: terminated reason=worker_budget_during_stall ", Assert.Single(harness.NoProgressEvents("terminated")).Rationale);
        var report = Assert.Single(harness.Reports.Published);
        Assert.True(report.BackendAuthored);
        Assert.Equal([NoProgressTerminationReport.LimitationWorkerBudgetDuringStall], report.Limitations);
    }

    [Fact]
    public void Create_GivesEachReasonItsOwnReservedSentence()
    {
        var reasons = Enum.GetValues<NoProgressTerminationReason>();
        var sentences = reasons.Select(reason => Assert.Single(NoProgressTerminationReport.Create([], reason).Limitations)).ToArray();

        Assert.Equal(reasons.Length, sentences.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(sentences.Order(StringComparer.Ordinal), NoProgressTerminationReport.ReservedLimitations.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(
            NoProgressTerminationReport.LimitationFor(NoProgressTerminationReason.NoWindowLeft) +
            NoProgressTerminationReport.LimitationFor(NoProgressTerminationReason.RecoveryNotAdmitted) +
            NoProgressTerminationReport.LimitationFor(NoProgressTerminationReason.TurnLimitDuringStall) +
            NoProgressTerminationReport.LimitationFor(NoProgressTerminationReason.WorkerBudgetDuringStall),
            "recovery allowance",
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_NoRoomForAnotherWindow_SkipsTheRecoveryAndTerminates()
    {
        // Five work turns and one reprompt: the stall is detected at turn 4 with two turns left, fewer
        // than the three a new window needs.
        var model = StallingModel();
        var harness = new InvestigationProgressTestHarness(model, _ => "{}", maxTurnsWithoutProgress: 2, maxTurns: 5);

        await harness.ProcessAsync();

        Assert.Equal(0, model.RecoveryCalls);
        Assert.StartsWith("no_progress: terminated reason=no_window_left ", Assert.Single(harness.NoProgressEvents("terminated")).Rationale);
    }

    [Theory]
    [InlineData("summary", "THE BACKEND ENDED THIS INVESTIGATION BECAUSE IT STOPPED MAKING PROGRESS; NO CONCLUSION WAS REACHED.")]
    [InlineData("summary", "The backend ended this investigation because it stopped making progress; no conclusion​ was reached. ")]
    [InlineData("summary", "Ｔhe backend ended this investigation because it stopped making progress; no conclusion was reached.")]
    [InlineData("summary", "Backend_Authored: all good.")]
    [InlineData("split-limitation-0", "")]
    [InlineData("split-limitation-1", "")]
    [InlineData("split-limitation-2", "")]
    [InlineData("split-limitation-3", "")]
    [InlineData("split-limitation-4", "")]
    public async Task ProcessAsync_ModelReportUsingReservedTextInDisguise_IsRefusedAndCorrected(string shape, string summary)
    {
        var limitations = shape.StartsWith("split-limitation-", StringComparison.Ordinal)
            ? SplitReservedLimitation(int.Parse(shape["split-limitation-".Length..], System.Globalization.CultureInfo.InvariantCulture))
            : ["The model's own limitation."];
        var forged = JsonSerializer.Serialize(new
        {
            report_json = new
            {
                status = "InsufficientEvidence",
                summary = shape == "summary" ? summary : "The model's own summary.",
                classification = "Unknown",
                confidence = "Low",
                documentationFit = "Missing",
                evidence = Array.Empty<object>(),
                limitations,
                recommendedNextAction = "Check the logs."
            }
        });
        var model = new ScriptedInvestigationModel(
            (call, _) => call == 1
                ? Response("Publish.", new AiToolCall("call-forged", OrchestratorToolNames.PublishReport, "v1", InvestigationProgressTestHarness.Json(forged)))
                : PublishTurn(),
            ProbeOnceThenAnswer);
        var harness = new InvestigationProgressTestHarness(model, _ => "{}");

        await harness.ProcessAsync();

        var report = Assert.Single(harness.Reports.Published);
        Assert.Equal("Investigation completed.", report.Summary);
        Assert.False(report.BackendAuthored);
        Assert.Contains(harness.Writer.Requests, row =>
            (row.Rationale ?? string.Empty).Contains(ReservedReportText.ReservedTextRefusal, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ProcessAsync_ModelLimitationMatchingAReservedSentenceLoosely_IsStripped(int reservedIndex)
    {
        var disguised = "​" + NoProgressTerminationReport.ReservedLimitations[reservedIndex].ToUpperInvariant().Replace(" ", "  ", StringComparison.Ordinal);
        var report = JsonSerializer.Serialize(new
        {
            report_json = new
            {
                status = "InsufficientEvidence",
                summary = "The model's own summary.",
                classification = "Unknown",
                confidence = "Low",
                documentationFit = "Missing",
                evidence = Array.Empty<object>(),
                limitations = new[] { disguised, "The model's own limitation." },
                recommendedNextAction = "Check the logs."
            }
        });
        var model = new ScriptedInvestigationModel(
            (_, _) => Response("Publish.", new AiToolCall("call-publish", OrchestratorToolNames.PublishReport, "v1", InvestigationProgressTestHarness.Json(report))),
            ProbeOnceThenAnswer);
        var harness = new InvestigationProgressTestHarness(model, _ => "{}");

        await harness.ProcessAsync();

        Assert.Equal(["The model's own limitation."], Assert.Single(harness.Reports.Published).Limitations);
    }

    [Fact]
    public void TheRecoverySuggestionCap_NeverSplitsASurrogatePair()
    {
        var text = new string('a', InvestigationRecoveryCall.MaxSuggestionLength - 1) + "\U0001F600" + "tail";

        var truncated = TextTruncator.Truncate(text, InvestigationRecoveryCall.MaxSuggestionLength);

        Assert.Equal(InvestigationRecoveryCall.MaxSuggestionLength - 1, truncated.Length);
        Assert.False(char.IsSurrogate(truncated[^1]));
        Assert.Equal("ab" + "\U0001F600", TextTruncator.Truncate("ab" + "\U0001F600" + "c", 4));
    }

    private static string[] SplitReservedLimitation(int reservedIndex)
    {
        var words = NoProgressTerminationReport.ReservedLimitations[reservedIndex].Split(' ');
        var half = words.Length / 2;
        return [string.Join(' ', words[..half]), string.Join(' ', words[half..])];
    }

    private static ScriptedInvestigationModel StallingModel(Func<int, AiModelRequest, AiModelResponse>? recovery = null) =>
        new(
            (call, _) => DelegateTurn("prober", "angle " + call),
            ProbeOnceThenAnswer,
            recovery);

    private static AiModelResponse ProbeOnceThenAnswer(int call, AiModelRequest request) =>
        request.Messages[^1].Role == AiMessageRole.Tool
            ? Response(WorkerOutput("Probe answered."))
            : ProbeTurn("{\"query\":\"pass " + call + "\"}");
}
