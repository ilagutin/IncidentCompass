using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The per-attempt progress record on its own: what counts as the same call, when a repeat is
/// unproductive, and what counts as progress.
/// </summary>
public sealed class InvestigationProgressTrackerTests
{
    [Fact]
    public void Fingerprint_WorkerToolArguments_AreComparedCanonically()
    {
        var first = EquivalentCallFingerprint.ForWorkerTool("probe", InvestigationProgressTestHarness.Json("""{"a":1,"b":{"c":"x"}}"""));
        var reordered = EquivalentCallFingerprint.ForWorkerTool("probe", InvestigationProgressTestHarness.Json("""{ "b": { "c": "x" }, "a": 1 }"""));
        var otherValue = EquivalentCallFingerprint.ForWorkerTool("probe", InvestigationProgressTestHarness.Json("""{"a":2,"b":{"c":"x"}}"""));
        var otherTool = EquivalentCallFingerprint.ForWorkerTool("probe2", InvestigationProgressTestHarness.Json("""{"a":1,"b":{"c":"x"}}"""));

        Assert.Equal(first, reordered);
        Assert.NotEqual(first, otherValue);
        Assert.NotEqual(first, otherTool);
        Assert.Matches("^[0-9a-f]{16}$", first.ShortHash);
    }

    [Fact]
    public void Fingerprint_DelegateTask_IgnoresWhitespaceButNotWordsOrRole()
    {
        var first = EquivalentCallFingerprint.ForDelegate("analysis", "Check the  queue.");
        Assert.Equal(first, EquivalentCallFingerprint.ForDelegate("analysis", "\n Check the queue.\t"));
        Assert.NotEqual(first, EquivalentCallFingerprint.ForDelegate("analysis", "Check the queues."));
        Assert.NotEqual(first, EquivalentCallFingerprint.ForDelegate("memory", "Check the queue."));

        // A role and a tool that share a name never share a fingerprint.
        Assert.NotEqual(
            EquivalentCallFingerprint.ForDelegate("probe", "{}"),
            EquivalentCallFingerprint.ForWorkerTool("probe", InvestigationProgressTestHarness.Json("{}")));
    }

    [Fact]
    public void RecordCallResult_ChangedResultResetsTheUnproductiveCount()
    {
        var tracker = new InvestigationProgressTracker(maxEquivalentCalls: 2, maxTurnsWithoutProgress: 4);
        var call = EquivalentCallFingerprint.ForWorkerTool("probe", InvestigationProgressTestHarness.Json("{}"));

        Assert.False(tracker.IsRepeatLimitReached(call, out _));
        tracker.RecordCallResult(call, "h1");
        tracker.RecordCallResult(call, "h1");
        Assert.False(tracker.IsRepeatLimitReached(call, out var afterOneRepeat));
        Assert.Equal(1, afterOneRepeat);

        tracker.RecordCallResult(call, "h2");
        Assert.False(tracker.IsRepeatLimitReached(call, out var afterChange));
        Assert.Equal(0, afterChange);

        tracker.RecordCallResult(call, "h2");
        tracker.RecordCallResult(call, "h2");
        Assert.True(tracker.IsRepeatLimitReached(call, out var atLimit));
        Assert.Equal(2, atLimit);
    }

    [Fact]
    public void CompleteTurn_NewEvidenceOrChangedClassificationIsProgress_RepeatedEvidenceIsNot()
    {
        var tracker = new InvestigationProgressTracker(maxEquivalentCalls: 2, maxTurnsWithoutProgress: 2);

        tracker.RecordEvidence("e1");
        tracker.RecordCandidateClassification("SimpleKnownError");
        Assert.True(tracker.CompleteTurn().MadeProgress);

        tracker.RecordEvidence("e1");
        tracker.RecordCandidateClassification("SimpleKnownError");
        Assert.Equal(new InvestigationTurnProgress(false, 1, false), tracker.CompleteTurn());

        tracker.RecordCandidateClassification("KnownIncident");
        Assert.Equal(new InvestigationTurnProgress(true, 0, false), tracker.CompleteTurn());

        Assert.False(tracker.CompleteTurn().LimitExceeded);
        Assert.False(tracker.CompleteTurn().LimitExceeded);
        Assert.Equal(new InvestigationTurnProgress(false, 3, true), tracker.CompleteTurn());

        tracker.ResetTurnsWithoutProgress();
        Assert.Equal(new InvestigationTurnProgress(false, 1, false), tracker.CompleteTurn());
        Assert.Equal(1, tracker.EvidenceCount);
        Assert.Equal("KnownIncident", tracker.CandidateClassification);
    }

    [Fact]
    public void For_UsesTheConfiguredBudget()
    {
        var tracker = InvestigationProgressTracker.For(
            new OrchestratorBudgetSettings(MaxWorkers: 1, MaxTokens: 1, MaxEquivalentCalls: 7, MaxTurnsWithoutProgress: 9));

        Assert.Equal(7, tracker.MaxEquivalentCalls);
        Assert.Equal(9, tracker.MaxTurnsWithoutProgress);
    }

    [Fact]
    public void IdentityOf_ReadsArtifactIdsOfThisAttemptAsTheContentTheyName()
    {
        var tracker = new InvestigationProgressTracker(maxEquivalentCalls: 2, maxTurnsWithoutProgress: 2);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var sameContentOtherId = Guid.NewGuid();
        var memoryItemId = Guid.NewGuid();
        tracker.RegisterArtifact(first, "content-a");
        tracker.RegisterArtifact(second, "content-b");
        tracker.RegisterArtifact(sameContentOtherId, "content-a");

        string Output(Guid artifactId, Guid itemId) =>
            "{\"items\":[{\"artifactId\":\"" + artifactId + "\",\"memoryItemId\":\"" + itemId + "\"}],\"note\":\"see artifact:" +
            artifactId.ToString().ToUpperInvariant() + "\"}";

        // Different ids naming the same content are the same result, also inside a reference and in
        // another letter case.
        Assert.Equal(tracker.IdentityOf(Output(first, memoryItemId)), tracker.IdentityOf(Output(sameContentOtherId, memoryItemId)));

        // Ids naming different content are different results.
        Assert.NotEqual(tracker.IdentityOf(Output(first, memoryItemId)), tracker.IdentityOf(Output(second, memoryItemId)));

        // A GUID no artifact of this attempt carries is data, not a per-call id.
        Assert.NotEqual(tracker.IdentityOf(Output(first, memoryItemId)), tracker.IdentityOf(Output(first, Guid.NewGuid())));

        // An id the attempt did not create is not substituted either.
        Assert.NotEqual(tracker.IdentityOf(Output(first, memoryItemId)), tracker.IdentityOf(Output(Guid.NewGuid(), memoryItemId)));
    }
}
