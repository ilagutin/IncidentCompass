using System.Text.Json;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Domain.Incidents.Statuses;
using static IncidentCompass.UnitTests.InvestigationProgressTestHarness;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Bounded recovery and honest termination over the real investigation loop. A stalled investigation
/// gets one tool-less recovery call that sees only a backend summary; when it stays stalled with no
/// recovery left, the backend publishes its own InsufficientEvidence report. A worker that keeps
/// proposing refused repeats is stopped without failing the attempt.
/// </summary>
public sealed class InvestigationRecoveryAndTerminationTests
{
    private const string RawToolOutput = "raw-tool-output-text";
    private const string RawArgument = "raw-argument-text";
    private const string RawTask = "raw-task-text";

    [Fact]
    public async Task ProcessAsync_StalledInvestigation_MakesExactlyOneToolLessRecoveryCallWhoseTextReachesTheOrchestrator()
    {
        var model = new ScriptedInvestigationModel(
            (call, _) => call <= 4 ? DelegateTurn("prober", RawTask + " " + call) : PublishTurn(),
            ProbeOnceThenAnswer);
        var harness = new InvestigationProgressTestHarness(
            model, _ => "{\"note\":\"" + RawToolOutput + "\"}", maxTurnsWithoutProgress: 2);

        await harness.ProcessAsync();

        Assert.Equal(1, model.RecoveryCalls);
        var request = Assert.Single(model.RecoveryRequests);
        Assert.Null(request.Tools);
        Assert.Equal(2, request.Messages.Count);
        Assert.Equal(AiMessageRole.System, request.Messages[0].Role);
        var summary = request.Messages[1].Content;
        Assert.Contains("Roles delegated to: prober", summary, StringComparison.Ordinal);
        Assert.Contains("Delegations run: 4", summary, StringComparison.Ordinal);
        Assert.Contains("Distinct evidence items: 2", summary, StringComparison.Ordinal);
        Assert.Contains("Current candidate classification: SimpleKnownError", summary, StringComparison.Ordinal);
        Assert.Contains(TriageInvestigationPromptBuilder.OrchestratorTaskInstruction, summary, StringComparison.Ordinal);
        foreach (var raw in new[] { RawToolOutput, RawArgument, RawTask })
        {
            Assert.DoesNotContain(raw, summary, StringComparison.Ordinal);
        }

        // The suggestion arrives as a marked user message in the orchestrator conversation, and the
        // orchestrator's fifth turn is the first one that is sent it.
        var suggestion = Assert.Single(
            model.OrchestratorRequests[4].Messages,
            message => message.Content.StartsWith(InvestigationNoProgressHandler.RecoverySuggestionPrefix, StringComparison.Ordinal));
        Assert.Equal(AiMessageRole.User, suggestion.Role);
        // The model's text follows the backend's prefix as one JSON string literal, never raw.
        Assert.Equal(
            InvestigationNoProgressHandler.RecoverySuggestionPrefix +
            ModelFacingJson.SerializeString(ScriptedInvestigationModel.DefaultRecoveryText),
            suggestion.Content);
        Assert.Equal(5, model.OrchestratorCalls);

        var recoveryCall = Assert.Single(harness.Writer.Requests, row =>
            row.EventType == TriageLedgerEventType.ModelCall && row.Rationale!.Contains("\"kind\":\"recovery\"", StringComparison.Ordinal));
        Assert.Contains("\"proposedToolCallCount\":0", recoveryCall.Rationale, StringComparison.Ordinal);
        var recoveryEvent = Assert.Single(harness.NoProgressEvents("recovery "));
        Assert.Equal(
            "no_progress: recovery recovery=1/1 evidence=2 suggestion_chars=" + ScriptedInvestigationModel.DefaultRecoveryText.Length,
            recoveryEvent.Rationale);
        Assert.Contains(harness.ProcessorLogger.Entries, entry => entry.EventId.Id == 3407);
        Assert.Equal(TriageReportStatus.Completed, Assert.Single(harness.Reports.Published).Status);
    }

    [Fact]
    public async Task ProcessAsync_StillStalledAfterRecovery_PublishesTheBackendInsufficientEvidenceReport()
    {
        var model = new ScriptedInvestigationModel(
            (call, _) => DelegateTurn("prober", "angle " + call),
            ProbeOnceThenAnswer);
        var harness = new InvestigationProgressTestHarness(model, _ => "{}", maxTurnsWithoutProgress: 2);

        await harness.ProcessAsync();

        // Window one (turns 2-4) spends the recovery, window two (turns 5-7) terminates.
        Assert.Equal(1, model.RecoveryCalls);
        Assert.Equal(7, model.OrchestratorCalls);
        AssertBackendReport(harness);
        var terminated = Assert.Single(harness.NoProgressEvents("terminated"));
        Assert.Equal(
            "no_progress: terminated reason=no_recovery_left recoveries_used=1 max_recoveries=1 turns=7 evidence=2 report=backend_authored",
            terminated.Rationale);
        Assert.Contains(harness.ProcessorLogger.Entries, entry => entry.EventId.Id == 3409);
    }

    [Fact]
    public async Task ProcessAsync_MaxRecoveriesZero_TerminatesWithoutARecoveryCall()
    {
        var model = new ScriptedInvestigationModel(
            (call, _) => DelegateTurn("prober", "angle " + call),
            ProbeOnceThenAnswer);
        var harness = new InvestigationProgressTestHarness(model, _ => "{}", maxTurnsWithoutProgress: 2, maxRecoveries: 0);

        await harness.ProcessAsync();

        Assert.Equal(0, model.RecoveryCalls);
        Assert.Equal(4, model.OrchestratorCalls);
        Assert.Empty(harness.NoProgressEvents("recovery"));
        AssertBackendReport(harness);
    }

    [Fact]
    public async Task ProcessAsync_RecoveryAnswerProposingATool_IsNeitherExecutedNorARecursion()
    {
        var model = new ScriptedInvestigationModel(
            (call, _) => DelegateTurn("prober", "angle " + call),
            ProbeOnceThenAnswer,
            (_, _) => DelegateTurn("prober", "a task the recovery call made up"));
        var harness = new InvestigationProgressTestHarness(model, _ => "{}", maxTurnsWithoutProgress: 2);

        await harness.ProcessAsync();

        // Only the recovery allowance decides how many recovery calls happen, and the proposal the
        // recovery answer carried started no worker: every Delegated entry is an orchestrator turn.
        Assert.Equal(1, model.RecoveryCalls);
        Assert.Equal(
            model.OrchestratorCalls,
            harness.Writer.Requests.Count(row => row.EventType == TriageLedgerEventType.Delegated));
        Assert.DoesNotContain(
            harness.Writer.Requests,
            row => (row.Rationale ?? string.Empty).Contains("made up", StringComparison.Ordinal));
        AssertBackendReport(harness);
    }

    [Fact]
    public async Task ProcessAsync_RecoveryCallFails_RecordsItsAccountingAndTerminates()
    {
        var model = new ScriptedInvestigationModel(
            (call, _) => DelegateTurn("prober", "angle " + call),
            ProbeOnceThenAnswer,
            (_, _) => throw new InvalidOperationException("The recovery provider broke its contract."));
        var harness = new InvestigationProgressTestHarness(model, _ => "{}", maxTurnsWithoutProgress: 2);

        await harness.ProcessAsync();

        // The failed recovery counts as no recovery left, so the first stall already terminates.
        Assert.Equal(1, model.RecoveryCalls);
        Assert.Equal(4, model.OrchestratorCalls);
        var failedCall = Assert.Single(harness.Writer.Requests, row =>
            row.EventType == TriageLedgerEventType.ModelCall && row.Rationale!.Contains("\"kind\":\"recovery\"", StringComparison.Ordinal));
        Assert.Contains("\"outcome\":\"failed\"", failedCall.Rationale, StringComparison.Ordinal);
        Assert.Matches(
            "^no_progress: recovery_failed recovery=1/1 error_code=[a-z0-9_]+$",
            Assert.Single(harness.NoProgressEvents("recovery_failed")).Rationale);
        Assert.Contains(harness.ProcessorLogger.Entries, entry => entry.EventId.Id == 3408);
        AssertBackendReport(harness);
    }

    [Fact]
    public async Task ProcessAsync_ModelAuthoredReportCarryingTheReservedSentence_IsPublishedWithoutIt()
    {
        var reportJson = JsonSerializer.Serialize(new
        {
            report_json = new
            {
                status = "InsufficientEvidence",
                summary = "The model's own summary.",
                classification = "Unknown",
                confidence = "Low",
                documentationFit = "Missing",
                evidence = Array.Empty<object>(),
                limitations = new[] { NoProgressTerminationReport.LimitationNoRecoveryLeft, "The model's own limitation." },
                recommendedNextAction = "Check the logs."
            }
        });
        var model = new ScriptedInvestigationModel(
            (_, _) => Response("Publish.", new AiToolCall("call-publish", OrchestratorToolNames.PublishReport, "v1", InvestigationProgressTestHarness.Json(reportJson))),
            ProbeOnceThenAnswer);
        var harness = new InvestigationProgressTestHarness(model, _ => "{}");

        await harness.ProcessAsync();

        var report = Assert.Single(harness.Reports.Published);
        Assert.Equal(["The model's own limitation."], report.Limitations);
        Assert.Empty(harness.NoProgressEvents("terminated"));
    }

    [Fact]
    public async Task ProcessAsync_WorkerThatKeepsProposingRefusedRepeats_IsStoppedAndTheOrchestratorCanAct()
    {
        var model = new ScriptedInvestigationModel(
            (call, _) => call == 1 ? DelegateTurn("prober", "Probe the checkout path.") : PublishTurn(),
            (_, _) => ProbeTurn("""{"query":"checkout"}"""));
        var harness = new InvestigationProgressTestHarness(model, _ => """{"status":"ok"}""", maxEquivalentCalls: 1);

        await harness.ProcessAsync();

        // Calls 1 and 2 run, 3 and 4 are refused in a row, and the run stops there.
        Assert.Equal(2, harness.Probe.Executions);
        Assert.Equal(4, model.WorkerCalls);
        using (var result = JsonDocument.Parse(model.OrchestratorToolMessages[0]))
        {
            Assert.Equal(WorkerStoppedRepeatingException.ErrorCode, result.RootElement.GetProperty("errorCode").GetString());
            Assert.Equal(WorkerStoppedRepeatingException.ErrorMessage, result.RootElement.GetProperty("errorMessage").GetString());
        }

        var stopped = Assert.Single(harness.NoProgressEvents("worker_stopped"));
        Assert.Equal("no_progress: worker_stopped role=prober consecutive_refusals=2", stopped.Rationale);
        Assert.Equal("prober", stopped.Role);
        Assert.Contains(harness.DelegateLogger.Entries, entry => entry.EventId.Id == 3406);
        Assert.DoesNotContain(harness.Writer.Requests, row => row.EventType == TriageLedgerEventType.WorkerCompleted);
        Assert.DoesNotContain(
            harness.Writer.Requests,
            row => (row.Rationale ?? string.Empty).StartsWith("worker_output_reprompt:", StringComparison.Ordinal));
        Assert.Equal(TriageReportStatus.Completed, Assert.Single(harness.Reports.Published).Status);
    }

    private static AiModelResponse ProbeOnceThenAnswer(int call, AiModelRequest request) =>
        request.Messages[^1].Role == AiMessageRole.Tool
            ? Response(WorkerOutput("Probe answered."))
            : ProbeTurn("{\"query\":\"" + RawArgument + " " + call + "\"}");

    private static void AssertBackendReport(InvestigationProgressTestHarness harness)
    {
        var report = Assert.Single(harness.Reports.Published);
        Assert.Equal(TriageReportStatus.InsufficientEvidence, report.Status);
        Assert.Equal("Unknown", report.Classification);
        Assert.Equal("Low", report.Confidence);
        Assert.Equal(DocumentationFitStatus.Missing, report.DocumentationFit);
        Assert.Equal(NoProgressTerminationReport.Summary, report.Summary);
        Assert.Equal(NoProgressTerminationReport.RecommendedNextAction, report.RecommendedNextAction);
        Assert.Equal([NoProgressTerminationReport.LimitationNoRecoveryLeft], report.Limitations);
        Assert.True(report.BackendAuthored);
        Assert.Single(harness.NoProgressEvents("terminated"));
    }
}
