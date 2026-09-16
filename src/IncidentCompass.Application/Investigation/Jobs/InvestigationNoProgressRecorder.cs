using System.Globalization;
using IncidentCompass.Domain.Incidents;
using Microsoft.Extensions.Logging;

namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// Makes each no-progress intervention audit-visible twice: one <c>BudgetEvent</c> whose rationale
/// opens with <see cref="RationalePrefix"/>, and one application log event. Both carry metadata only:
/// role and tool names, the short fingerprint hash and counts. Never arguments, task text or output.
/// </summary>
/// <remarks>
/// The <c>no_progress:</c> reasons are deliberately separate from the time-based limits
/// (<c>wall_clock_limit_reached</c>, <c>tool_execution_timeout</c> and the provider stall codes), which
/// stay the only signals for something slow. Nothing here reads a clock.
/// </remarks>
internal sealed partial class InvestigationNoProgressRecorder(TriageLedgerAppender ledgerAppender, ILogger logger)
{
    internal const string RationalePrefix = "no_progress: ";

    internal const string RepeatedCallReason = "repeated_call";

    internal const string TurnsWithoutProgressReason = "turns_without_progress";

    /// <summary>The error code the caller of a refused equivalent call receives.</summary>
    internal const string RepeatedCallErrorCode = "repeated_call_without_new_evidence";

    /// <summary>
    /// The fixed message the caller of a refused equivalent call receives. It names no argument and
    /// no task, so nothing model-authored is echoed back.
    /// </summary>
    internal const string RepeatedCallErrorMessage =
        "This call was not executed: an earlier call in this attempt returned the same result; " +
        "use it or change the request.";

    public async Task RecordRepeatedCallAsync(
        TriageJob job,
        string roleName,
        string toolName,
        EquivalentCallFingerprint fingerprint,
        int unproductiveRepeats,
        int maxEquivalentCalls,
        CancellationToken cancellationToken)
    {
        LogRepeatedCallRefused(
            logger, job.Id, job.Attempt, roleName, toolName, fingerprint.ShortHash, unproductiveRepeats, maxEquivalentCalls);
        await ledgerAppender.AppendNoProgressBudgetEventAsync(
            job,
            roleName,
            toolName,
            RationalePrefix + RepeatedCallReason +
            " role=" + roleName +
            " tool=" + toolName +
            " fingerprint=" + fingerprint.ShortHash +
            " unproductive_repeats=" + unproductiveRepeats.ToString(CultureInfo.InvariantCulture) +
            " max_equivalent_calls=" + maxEquivalentCalls.ToString(CultureInfo.InvariantCulture),
            cancellationToken);
    }

    public async Task RecordTurnsWithoutProgressAsync(
        TriageJob job,
        InvestigationProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        LogTurnsWithoutProgress(
            logger, job.Id, job.Attempt, tracker.TurnsWithoutProgress, tracker.MaxTurnsWithoutProgress, tracker.EvidenceCount);
        await ledgerAppender.AppendNoProgressBudgetEventAsync(
            job,
            "orchestrator",
            toolName: null,
            RationalePrefix + TurnsWithoutProgressReason +
            " turns=" + tracker.TurnsWithoutProgress.ToString(CultureInfo.InvariantCulture) +
            " max_turns_without_progress=" + tracker.MaxTurnsWithoutProgress.ToString(CultureInfo.InvariantCulture) +
            " evidence=" + tracker.EvidenceCount.ToString(CultureInfo.InvariantCulture),
            cancellationToken);
    }

    [LoggerMessage(
        EventId = 3404,
        Level = LogLevel.Warning,
        Message = "Equivalent call {ToolName} for role {Role} on triage job {JobId} attempt {Attempt} was refused without new evidence (fingerprint {Fingerprint}, {UnproductiveRepeats}/{MaxEquivalentCalls}).")]
    private static partial void LogRepeatedCallRefused(
        ILogger logger,
        Guid jobId,
        int attempt,
        string role,
        string toolName,
        string fingerprint,
        int unproductiveRepeats,
        int maxEquivalentCalls);

    [LoggerMessage(
        EventId = 3405,
        Level = LogLevel.Warning,
        Message = "Orchestrator for triage job {JobId} attempt {Attempt} made no progress for {TurnsWithoutProgress} consecutive turns (limit {MaxTurnsWithoutProgress}, evidence {EvidenceCount}).")]
    private static partial void LogTurnsWithoutProgress(
        ILogger logger,
        Guid jobId,
        int attempt,
        int turnsWithoutProgress,
        int maxTurnsWithoutProgress,
        int evidenceCount);
}
