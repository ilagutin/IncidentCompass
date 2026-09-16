using System.Text.Json;
using IncidentCompass.Application.Intake.Configuration;

namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// The in-memory progress record of one investigation attempt. The processor creates one per attempt
/// and hands it to every delegate and worker tool call of that attempt; nothing in it is persisted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Equivalent calls.</b> For each <see cref="EquivalentCallFingerprint"/> it keeps the
/// <see cref="EvidenceResultIdentity"/> of the last result and how many repeats in a row returned that same result. A repeat whose result differs
/// from the previous one is a justified recheck and resets the count. Once the count reaches
/// <see cref="MaxEquivalentCalls"/>, the next equivalent call is refused before it runs.
/// </para>
/// <para>
/// <b>Progress.</b> A turn makes progress when it adds an evidence identity (a tool result or a
/// worker output, identified by <see cref="EvidenceResultIdentity"/>) not seen before in the
/// attempt, or changes the latest candidate classification. Time is not an input and no order of roles is prescribed, so a slow model that keeps producing
/// never counts as a turn without progress.
/// </para>
/// </remarks>
internal sealed class InvestigationProgressTracker(int maxEquivalentCalls, int maxTurnsWithoutProgress)
{
    private readonly Dictionary<string, (string LastResultHash, int UnproductiveRepeats)> calls =
        new(StringComparer.Ordinal);

    private readonly HashSet<string> evidenceHashes = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> artifactIdentities = new(StringComparer.OrdinalIgnoreCase);

    private bool progressThisTurn;

    public int MaxEquivalentCalls { get; } = maxEquivalentCalls;

    public int MaxTurnsWithoutProgress { get; } = maxTurnsWithoutProgress;

    public int TurnsWithoutProgress { get; private set; }

    /// <summary>
    /// Whether a stall is open: the no-progress window was exceeded at least once in this attempt and
    /// no turn has made progress since. Resetting the window after a recovery does not close it; only
    /// progress does.
    /// </summary>
    public bool StallDetected { get; private set; }

    public int EvidenceCount => evidenceHashes.Count;

    public string? CandidateClassification { get; private set; }

    /// <summary>What the attempt has done so far, for the recovery summary and the termination audit.</summary>
    public InvestigationActivity Activity { get; } = new();

    public static InvestigationProgressTracker For(OrchestratorBudgetSettings budget) =>
        new(budget.MaxEquivalentCalls, budget.MaxTurnsWithoutProgress);

    /// <summary>
    /// Whether an equivalent call has already returned the same result
    /// <see cref="MaxEquivalentCalls"/> times in a row, so this one would add no new evidence.
    /// </summary>
    public bool IsRepeatLimitReached(EquivalentCallFingerprint fingerprint, out int unproductiveRepeats)
    {
        unproductiveRepeats = calls.TryGetValue(fingerprint.Key, out var history) ? history.UnproductiveRepeats : 0;
        return unproductiveRepeats >= MaxEquivalentCalls;
    }

    /// <summary>
    /// Remembers what an artifact created in this attempt says, so a later result that names the
    /// artifact by id is identified by that content rather than by the id. See
    /// <see cref="EvidenceResultIdentity"/>.
    /// </summary>
    public void RegisterArtifact(Guid artifactId, string identity) =>
        artifactIdentities[artifactId.ToString()] = identity;

    /// <summary>The <see cref="EvidenceResultIdentity"/> of a redacted payload.</summary>
    public string IdentityOf(JsonElement redactedPayload) =>
        EvidenceResultIdentity.Compute(redactedPayload, artifactIdentities);

    /// <summary>The <see cref="EvidenceResultIdentity"/> of a serialized result, such as a tool failure message.</summary>
    public string IdentityOf(string json) =>
        EvidenceResultIdentity.Compute(json, artifactIdentities);

    /// <summary>Records the identity of what a call returned, success or failure alike.</summary>
    public void RecordCallResult(EquivalentCallFingerprint fingerprint, string resultHash)
    {
        if (!calls.TryGetValue(fingerprint.Key, out var history))
        {
            calls[fingerprint.Key] = (resultHash, 0);
            return;
        }

        calls[fingerprint.Key] = string.Equals(history.LastResultHash, resultHash, StringComparison.Ordinal)
            ? (resultHash, history.UnproductiveRepeats + 1)
            : (resultHash, 0);
    }

    /// <summary>Records the identity of a stored tool result or worker output.</summary>
    public void RecordEvidence(string contentHash)
    {
        if (evidenceHashes.Add(contentHash))
        {
            progressThisTurn = true;
        }
    }

    /// <summary>Records the candidate classification an analysis worker output named.</summary>
    public void RecordCandidateClassification(string? classification)
    {
        if (string.IsNullOrWhiteSpace(classification) ||
            string.Equals(CandidateClassification, classification, StringComparison.Ordinal))
        {
            return;
        }

        CandidateClassification = classification;
        progressThisTurn = true;
    }

    /// <summary>Closes the current orchestrator turn and starts the next one.</summary>
    public InvestigationTurnProgress CompleteTurn()
    {
        var madeProgress = progressThisTurn;
        progressThisTurn = false;
        Activity.RecordTurnCompleted();
        TurnsWithoutProgress = madeProgress ? 0 : TurnsWithoutProgress + 1;
        var limitExceeded = TurnsWithoutProgress > MaxTurnsWithoutProgress;
        StallDetected = !madeProgress && (StallDetected || limitExceeded);
        return new InvestigationTurnProgress(
            madeProgress,
            TurnsWithoutProgress,
            limitExceeded);
    }

    /// <summary>
    /// Starts a new window of turns without progress after the processor has acted on an exceeded
    /// limit, so one stall is reported once rather than on every later turn.
    /// </summary>
    public void ResetTurnsWithoutProgress() => TurnsWithoutProgress = 0;
}
