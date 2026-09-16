using System.Text.Json;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Domain.Incidents.Statuses;
using static IncidentCompass.UnitTests.InvestigationProgressTestHarness;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Repetition and progress detection over the real investigation loop. An equivalent call that keeps
/// returning the same result is refused past <c>MaxEquivalentCalls</c> without failing the attempt, a
/// repeat whose data changed is allowed, and consecutive turns without new evidence or a changed
/// classification past <c>MaxTurnsWithoutProgress</c> are recorded once per window while the attempt
/// goes on. Time is not an input: a slow model that keeps producing records nothing.
/// </summary>
public sealed class InvestigationRepetitionAndProgressTests
{
    private const string ProbeArguments = """{"query":"checkout secret-argument-text","limit":5}""";

    [Fact]
    public async Task ProcessAsync_IdenticalProbeWithUnchangedResult_IsRefusedPastTheLimitAndTheAttemptContinues()
    {
        var model = new ScriptedInvestigationModel(
            (call, _) => call == 1 ? DelegateTurn("prober", "Probe the checkout path.") : PublishTurn(),
            (call, _) => call <= 4 ? ProbeTurn(ProbeArguments) : Response(WorkerOutput("Probe answered.")));
        var harness = new InvestigationProgressTestHarness(model, _ => """{"status":"ok"}""");

        await harness.ProcessAsync();

        // Call 1 is new, calls 2 and 3 repeat it with the same result (two unproductive repeats, the
        // default limit), so call 4 is refused before it runs.
        Assert.Equal(3, harness.Probe.Executions);
        using (var refusal = JsonDocument.Parse(model.WorkerToolMessages[3]))
        {
            Assert.Equal("NotExecuted", refusal.RootElement.GetProperty("status").GetString());
            Assert.Equal(
                InvestigationNoProgressRecorder.RepeatedCallErrorCode,
                refusal.RootElement.GetProperty("errorCode").GetString());
        }

        var budgetEvent = Assert.Single(harness.NoProgressEvents("repeated_call"));
        Assert.Equal("prober", budgetEvent.Role);
        Assert.Equal(ProbeToolName, budgetEvent.ToolName);
        Assert.Matches(
            "^no_progress: repeated_call role=prober tool=probe fingerprint=[0-9a-f]{16} unproductive_repeats=2 max_equivalent_calls=2$",
            budgetEvent.Rationale);
        Assert.DoesNotContain("secret-argument-text", budgetEvent.Rationale, StringComparison.Ordinal);
        var log = harness.ToolLogger.Single(3404);
        Assert.DoesNotContain("secret-argument-text", log.Message, StringComparison.Ordinal);

        // The refusal is not a failure: the worker answered, the orchestrator published.
        Assert.Equal(5, model.WorkerCalls);
        Assert.Single(harness.Reports.Published);
    }

    [Fact]
    public async Task ProcessAsync_IdenticalProbeWhoseResultChanges_IsAJustifiedRecheck()
    {
        var model = new ScriptedInvestigationModel(
            (call, _) => call == 1 ? DelegateTurn("prober", "Poll the checkout queue.") : PublishTurn(),
            (call, _) => call <= 5 ? ProbeTurn(ProbeArguments) : Response(WorkerOutput("Queue drained.")));
        var harness = new InvestigationProgressTestHarness(model, execution => "{\"depth\":" + execution + "}");

        await harness.ProcessAsync();

        Assert.Equal(5, harness.Probe.Executions);
        Assert.Empty(harness.NoProgressEvents("repeated_call"));
        Assert.DoesNotContain(
            model.WorkerToolMessages,
            message => message.Contains(InvestigationNoProgressRecorder.RepeatedCallErrorCode, StringComparison.Ordinal));
        Assert.Single(harness.Reports.Published);
    }

    [Fact]
    public async Task ProcessAsync_EquivalentDelegateWithUnchangedOutput_IsRefusedWithoutRunningAWorkerOrChargingAReprompt()
    {
        // Whitespace differences do not make a different question.
        string[] tasks = ["Analyze the checkout timeout.", "  Analyze the checkout\ttimeout. ", "Analyze  the checkout timeout.", "Analyze the checkout timeout."];
        var model = new ScriptedInvestigationModel(
            (call, _) => call <= tasks.Length ? DelegateTurn("analysis", tasks[call - 1]) : PublishTurn(),
            (_, _) => Response(WorkerOutput("The checkout service timed out.")));
        var harness = new InvestigationProgressTestHarness(model, _ => "{}");

        await harness.ProcessAsync();

        Assert.Equal(3, model.WorkerCalls);
        using (var refusal = JsonDocument.Parse(model.OrchestratorToolMessages[3]))
        {
            Assert.Equal(
                InvestigationNoProgressRecorder.RepeatedCallErrorCode,
                refusal.RootElement.GetProperty("errorCode").GetString());
            Assert.Equal(
                InvestigationNoProgressRecorder.RepeatedCallErrorMessage,
                refusal.RootElement.GetProperty("errorMessage").GetString());
        }

        var budgetEvent = Assert.Single(harness.NoProgressEvents("repeated_call"));
        Assert.Equal("analysis", budgetEvent.Role);
        Assert.Equal(OrchestratorToolNames.Delegate, budgetEvent.ToolName);
        Assert.DoesNotContain("checkout", budgetEvent.Rationale, StringComparison.Ordinal);
        Assert.Contains(harness.DelegateLogger.Entries, entry => entry.EventId.Id == 3404);
        Assert.Equal(3, harness.Writer.Requests.Count(request => request.EventType == TriageLedgerEventType.Delegated));
        Assert.DoesNotContain(
            harness.Writer.Requests,
            request => (request.Rationale ?? string.Empty).StartsWith("orchestrator_reprompt:", StringComparison.Ordinal));
        Assert.Single(harness.Reports.Published);
    }

    [Fact]
    public async Task ProcessAsync_TurnsWithoutProgressPastTheLimit_RecordsOneEventPerWindowAndContinues()
    {
        // Every delegate asks something new, so nothing is refused, but every worker answers the same
        // thing: turn 1 adds evidence and turns 2 to 7 add nothing.
        var model = new ScriptedInvestigationModel(
            (call, _) => call <= 7 ? DelegateTurn("analysis", "Look again, angle " + call + ".") : PublishTurn(),
            (_, _) => Response(WorkerOutput("The checkout service timed out.")));
        var harness = new InvestigationProgressTestHarness(model, _ => "{}", maxEquivalentCalls: 10, maxTurnsWithoutProgress: 2);

        await harness.ProcessAsync();

        // Turns 2-4 are the first window (3 > 2 at turn 4); the count restarts, and turns 5-7 are the
        // second. The interim action is to continue, so the report is still published.
        var events = harness.NoProgressEvents("turns_without_progress");
        Assert.Equal(2, events.Length);
        Assert.All(events, budgetEvent =>
        {
            Assert.Equal("orchestrator", budgetEvent.Role);
            Assert.Null(budgetEvent.ToolName);
            Assert.Equal(
                "no_progress: turns_without_progress turns=3 max_turns_without_progress=2 evidence=1",
                budgetEvent.Rationale);
        });
        Assert.Equal(2, harness.ProcessorLogger.Entries.Count(entry => entry.EventId.Id == 3405));
        Assert.Empty(harness.NoProgressEvents("repeated_call"));
        Assert.Equal(8, model.OrchestratorCalls);
        Assert.Single(harness.Reports.Published);
    }

    [Fact]
    public async Task ProcessAsync_SlowModelThatKeepsProducing_RecordsNoNoProgressEvent()
    {
        // Every call costs ten minutes of clock time and every worker answer names a new fact.
        InvestigationProgressTestHarness? harness = null;
        var model = new ScriptedInvestigationModel(
            (call, _) =>
            {
                harness!.Time.Advance(TimeSpan.FromMinutes(10));
                return call <= 6 ? DelegateTurn("analysis", "Step " + call + ".") : PublishTurn();
            },
            (call, _) =>
            {
                harness!.Time.Advance(TimeSpan.FromMinutes(10));
                return Response(WorkerOutput("Fact " + call + "."));
            });
        harness = new InvestigationProgressTestHarness(model, _ => "{}", maxTurnsWithoutProgress: 2);
        var startedAt = harness.Time.GetUtcNow();

        await harness.ProcessAsync();

        Assert.True(harness.Time.GetUtcNow() - startedAt >= TimeSpan.FromHours(2));
        Assert.Empty(harness.NoProgressEvents(string.Empty));
        Assert.DoesNotContain(harness.ProcessorLogger.Entries, entry => entry.EventId.Id is 3404 or 3405);
        Assert.Single(harness.Reports.Published);
    }
    [Fact]
    public async Task ProcessAsync_ArtifactEmittingToolWithTheSameMatches_IsRefusedDespiteFreshArtifactIds()
    {
        var model = new ScriptedInvestigationModel(
            (call, _) => call == 1 ? DelegateTurn("prober", "Search memory.") : PublishTurn(),
            (call, _) => call <= 4 ? ProbeTurn(ProbeArguments) : Response(WorkerOutput("Runbook found.")));
        var harness = new InvestigationProgressTestHarness(model, _ => "checkout runbook", probeEmitsArtifacts: true);

        await harness.ProcessAsync();

        // Every execution names a different artifactId, and the result is still the same result.
        Assert.Equal(3, harness.Probe.Executions);
        Assert.Equal(3, model.WorkerToolMessages.Take(3).Select(ArtifactIdOf).Distinct().Count());
        Assert.Contains(InvestigationNoProgressRecorder.RepeatedCallErrorCode, model.WorkerToolMessages[3], StringComparison.Ordinal);
        Assert.Single(harness.NoProgressEvents("repeated_call"));
    }

    [Fact]
    public async Task ProcessAsync_ArtifactEmittingToolWithTheSameMatches_AddsNoNewEvidence()
    {
        // Each delegate searches with different arguments, so nothing is refused, and every search
        // finds the same match under a fresh artifact id. Only turn 1 adds evidence.
        var model = new ScriptedInvestigationModel(
            (call, _) => call <= 4 ? DelegateTurn("prober", "Search memory, pass " + call + ".") : PublishTurn(),
            (call, _) => call % 2 == 1
                ? ProbeTurn("{\"query\":\"pass " + call + "\"}")
                : Response(WorkerOutput("Runbook found.")));
        var harness = new InvestigationProgressTestHarness(
            model, _ => "checkout runbook", probeEmitsArtifacts: true, maxTurnsWithoutProgress: 2);

        await harness.ProcessAsync();

        Assert.Equal(4, harness.Probe.Executions);
        var budgetEvent = Assert.Single(harness.NoProgressEvents("turns_without_progress"));
        Assert.Equal(
            "no_progress: turns_without_progress turns=3 max_turns_without_progress=2 evidence=2",
            budgetEvent.Rationale);
    }

    [Fact]
    public async Task ProcessAsync_ArtifactEmittingToolWithDifferentMatches_ResetsTheRepeatAndMakesProgress()
    {
        var model = new ScriptedInvestigationModel(
            (call, _) => call <= 4 ? DelegateTurn("prober", "Poll memory, pass " + call + ".") : PublishTurn(),
            (call, _) => call % 2 == 1 ? ProbeTurn(ProbeArguments) : Response(WorkerOutput("Runbook found.")));
        var harness = new InvestigationProgressTestHarness(
            model, execution => "runbook revision " + execution, probeEmitsArtifacts: true, maxEquivalentCalls: 1, maxTurnsWithoutProgress: 2);

        await harness.ProcessAsync();

        // The same probe ran four times under MaxEquivalentCalls 1 because every result changed, and
        // every turn added evidence.
        Assert.Equal(4, harness.Probe.Executions);
        Assert.Empty(harness.NoProgressEvents(string.Empty));
    }

    [Fact]
    public async Task ProcessAsync_EquivalentDelegateWhoseOutputOnlyEchoesFreshArtifactIds_IsRefused()
    {
        // The worker reads its probe result and cites the match by id, as a memory worker does. Each
        // delegate's probe is a new artifact, so the outputs differ only in the id they cite.
        var model = new ScriptedInvestigationModel(
            (call, _) => call <= 4 ? DelegateTurn("prober", "Search memory.") : PublishTurn(),
            (call, request) => request.Messages[^1].Role == IncidentCompass.Application.Core.ModelClients.AiMessageRole.Tool
                ? Response(WorkerOutput("See artifact:" + ArtifactIdOf(request.Messages[^1].Content) + "."))
                : ProbeTurn("{\"query\":\"delegate " + call + "\"}"));
        var harness = new InvestigationProgressTestHarness(
            model, _ => "checkout runbook", probeEmitsArtifacts: true, maxTurnsWithoutProgress: 10);

        await harness.ProcessAsync();

        Assert.Equal(3, harness.Probe.Executions);
        Assert.Equal(6, model.WorkerCalls);
        Assert.Contains(InvestigationNoProgressRecorder.RepeatedCallErrorCode, model.OrchestratorToolMessages[3], StringComparison.Ordinal);
        var budgetEvent = Assert.Single(harness.NoProgressEvents("repeated_call"));
        Assert.Equal(OrchestratorToolNames.Delegate, budgetEvent.ToolName);
    }

    private static string ArtifactIdOf(string toolMessage)
    {
        using var document = JsonDocument.Parse(toolMessage);
        return document.RootElement.GetProperty("items")[0].GetProperty("artifactId").GetString()!;
    }
}
