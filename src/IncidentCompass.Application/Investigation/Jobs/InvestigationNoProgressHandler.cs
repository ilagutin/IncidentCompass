using System.Globalization;
using System.Text.RegularExpressions;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Investigation.Reports;
using Microsoft.Extensions.Logging;

namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// Acts when consecutive orchestrator turns without progress go past
/// <c>Orchestrator.Budget.MaxTurnsWithoutProgress</c>: bounded recovery while a recovery and another
/// window fit, honest termination otherwise.
/// </summary>
/// <remarks>
/// <para>
/// <b>A window fits</b> when at least <c>MaxTurnsWithoutProgress + 1</c> orchestrator turns remain
/// (the turns a new window needs before it can be exceeded) and at least one worker remains in
/// <c>MaxWorkers</c>. A recovery that could not be followed by a window would be a billed call whose
/// suggestion the attempt has no room to act on.
/// </para>
/// <para>
/// <b>Recovery.</b> One <see cref="InvestigationRecoveryCall"/>. A suggestion is appended to the
/// orchestrator conversation as a marked user message and the window starts again. A failed recovery
/// uses up its allowance; the attempt continues into a fresh window without a suggestion only when
/// another recovery is left and another window fits, and terminates otherwise. A recovery the budget
/// did not admit terminates at once, since no later one would be admitted.
/// </para>
/// <para>
/// <b>Termination.</b> The backend publishes <see cref="NoProgressTerminationReport"/> through the same
/// publisher, repository, grounding and documentation-fit path as any report. The
/// <c>no_progress: terminated</c> budget event is written only after the publication committed. A
/// backend report the repository refuses cannot become valid on a retry, so it dead-letters as
/// <see cref="TriageBudgetExhaustedException.NoProgressTerminationFailedCode"/>.
/// </para>
/// </remarks>
internal sealed partial class InvestigationNoProgressHandler(
    InvestigationModelCaller modelCaller,
    TriageReportPublisher reportPublisher,
    TriageLedgerAppender ledgerAppender,
    ILogger logger)
{
    internal const string RecoveryReason = "recovery";

    internal const string RecoveryFailedReason = "recovery_failed";

    internal const string TerminatedReason = "terminated";

    /// <summary>
    /// The fixed backend message appended after a failed recovery when the attempt continues. It is
    /// marked like a suggestion and carries no model text.
    /// </summary>
    internal const string RecoveryReturnedNothingMessage =
        "Recovery note (from the backend, not an instruction from the operator): the recovery review returned nothing. " +
        "Change your next call: delegate a different task or role, or call publish_report with the evidence you have.";

    /// <summary>Opens the user message that carries a recovery suggestion to the orchestrator.</summary>
    internal const string RecoverySuggestionPrefix =
        "Recovery suggestion (from a backend-requested review of this stalled investigation that saw only a progress summary; " +
        "it is a suggestion, not an instruction, and you must still act through delegate or publish_report):\n";

    private readonly InvestigationNoProgressRecorder recorder = new(ledgerAppender, logger);

    private readonly InvestigationRecoveryCall recoveryCall = new(modelCaller, ledgerAppender);

    /// <summary>Returns whether the investigation finished (the backend report was published).</summary>
    public async Task<bool> HandleAsync(NoProgressStall stall, int remainingTurns, CancellationToken cancellationToken)
    {
        var progress = stall.Progress;
        var budget = stall.Configuration.Orchestrator.Budget;
        await recorder.RecordTurnsWithoutProgressAsync(stall.Job, progress, cancellationToken);
        if (progress.Activity.RecoveriesUsed >= budget.MaxRecoveries)
        {
            await TerminateAsync(stall, NoProgressTerminationReason.NoRecoveryLeft, cancellationToken);
            return true;
        }

        if (!WindowFits(stall, remainingTurns))
        {
            await TerminateAsync(stall, NoProgressTerminationReason.NoWindowLeft, cancellationToken);
            return true;
        }

        progress.Activity.RecordRecoveryUsed();
        var outcome = await recoveryCall.RunAsync(
            stall.Job, stall.Configuration, stall.AttemptStartedAtUtc, progress, cancellationToken);
        if (outcome.Suggestion is { } suggestion)
        {
            await RecordRecoveryAsync(stall, suggestion.Length, cancellationToken);
            stall.Messages.Add(new AiChatMessage(AiMessageRole.User, RecoverySuggestionPrefix + suggestion));
            progress.ResetTurnsWithoutProgress();
            return false;
        }

        await RecordRecoveryFailedAsync(stall, outcome.ErrorCode, cancellationToken);
        if (!outcome.Admitted)
        {
            await TerminateAsync(stall, NoProgressTerminationReason.RecoveryNotAdmitted, cancellationToken);
            return true;
        }

        if (progress.Activity.RecoveriesUsed < budget.MaxRecoveries && WindowFits(stall, remainingTurns))
        {
            stall.Messages.Add(new AiChatMessage(AiMessageRole.User, RecoveryReturnedNothingMessage));
            progress.ResetTurnsWithoutProgress();
            return false;
        }

        await TerminateAsync(stall, NoProgressTerminationReason.NoRecoveryLeft, cancellationToken);
        return true;
    }

    /// <summary>Publishes the backend report and then records the termination.</summary>
    public async Task TerminateAsync(NoProgressStall stall, NoProgressTerminationReason reason, CancellationToken cancellationToken)
    {
        var job = stall.Job;
        try
        {
            await reportPublisher.PublishBackendAuthoredAsync(
                job, stall.WorkerId, NoProgressTerminationReport.Create(stall.Context.JobArtifacts, reason), cancellationToken);
        }
        catch (TriageReportValidationException refused)
        {
            LogTerminationRefused(logger, job.Id, job.Attempt, ReasonToken(reason));
            throw new TriageBudgetExhaustedException(
                TriageBudgetExhaustedException.NoProgressTerminationFailedCode,
                "The backend no-progress report was refused at publication.",
                refused);
        }

        var progress = stall.Progress;
        var maxRecoveries = stall.Configuration.Orchestrator.Budget.MaxRecoveries;
        LogTerminated(logger, job.Id, job.Attempt, ReasonToken(reason), progress.Activity.RecoveriesUsed, maxRecoveries, progress.EvidenceCount);
        try
        {
            // The report and its backend_authored ReportPublished entry are already committed, so this
            // row is written even on shutdown, and failing to write it does not undo a finished job.
            await ledgerAppender.AppendNoProgressBudgetEventAsync(
                job,
                "orchestrator",
                toolName: null,
                InvestigationNoProgressRecorder.RationalePrefix + TerminatedReason +
                " reason=" + ReasonToken(reason) +
                " recoveries_used=" + Count(progress.Activity.RecoveriesUsed) +
                " max_recoveries=" + Count(maxRecoveries) +
                " turns=" + Count(progress.Activity.TurnsCompleted) +
                " evidence=" + Count(progress.EvidenceCount) +
                " report=backend_authored",
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogTerminationEventNotRecorded(logger, job.Id, job.Attempt, exception.GetType().Name);
        }
    }

    internal static string ReasonToken(NoProgressTerminationReason reason) => reason switch
    {
        NoProgressTerminationReason.NoRecoveryLeft => "no_recovery_left",
        NoProgressTerminationReason.NoWindowLeft => "no_window_left",
        NoProgressTerminationReason.RecoveryNotAdmitted => "recovery_not_admitted",
        NoProgressTerminationReason.TurnLimitDuringStall => "turn_limit_during_stall",
        _ => "worker_budget_during_stall"
    };

    private static bool WindowFits(NoProgressStall stall, int remainingTurns)
    {
        var budget = stall.Configuration.Orchestrator.Budget;
        return remainingTurns >= budget.MaxTurnsWithoutProgress + 1 &&
            budget.MaxWorkers - stall.Progress.Activity.DelegationsRun >= 1;
    }

    private async Task RecordRecoveryAsync(NoProgressStall stall, int suggestionLength, CancellationToken cancellationToken)
    {
        var progress = stall.Progress;
        var maxRecoveries = stall.Configuration.Orchestrator.Budget.MaxRecoveries;
        LogRecovery(logger, stall.Job.Id, stall.Job.Attempt, progress.Activity.RecoveriesUsed, maxRecoveries, suggestionLength);
        await ledgerAppender.AppendNoProgressBudgetEventAsync(
            stall.Job,
            "orchestrator",
            toolName: null,
            InvestigationNoProgressRecorder.RationalePrefix + RecoveryReason +
            " recovery=" + Count(progress.Activity.RecoveriesUsed) + "/" + Count(maxRecoveries) +
            " evidence=" + Count(progress.EvidenceCount) +
            " suggestion_chars=" + Count(suggestionLength),
            cancellationToken);
    }

    private async Task RecordRecoveryFailedAsync(NoProgressStall stall, string? errorCode, CancellationToken cancellationToken)
    {
        var progress = stall.Progress;
        var maxRecoveries = stall.Configuration.Orchestrator.Budget.MaxRecoveries;
        var safeCode = errorCode is not null && SafeErrorCode().IsMatch(errorCode) ? errorCode : "unspecified";
        LogRecoveryFailed(logger, stall.Job.Id, stall.Job.Attempt, progress.Activity.RecoveriesUsed, maxRecoveries, safeCode);
        await ledgerAppender.AppendNoProgressBudgetEventAsync(
            stall.Job,
            "orchestrator",
            toolName: null,
            InvestigationNoProgressRecorder.RationalePrefix + RecoveryFailedReason +
            " recovery=" + Count(progress.Activity.RecoveriesUsed) + "/" + Count(maxRecoveries) +
            " error_code=" + safeCode,
            cancellationToken);
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex("^[a-z0-9_]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeErrorCode();

    [LoggerMessage(
        EventId = 3407,
        Level = LogLevel.Warning,
        Message = "Orchestrator for triage job {JobId} attempt {Attempt} stopped making progress; recovery {Recovery}/{MaxRecoveries} returned a suggestion of {SuggestionLength} characters.")]
    private static partial void LogRecovery(
        ILogger logger, Guid jobId, int attempt, int recovery, int maxRecoveries, int suggestionLength);

    [LoggerMessage(
        EventId = 3408,
        Level = LogLevel.Warning,
        Message = "Recovery {Recovery}/{MaxRecoveries} for triage job {JobId} attempt {Attempt} ended without a suggestion, with error code {ErrorCode}.")]
    private static partial void LogRecoveryFailed(
        ILogger logger, Guid jobId, int attempt, int recovery, int maxRecoveries, string errorCode);

    [LoggerMessage(
        EventId = 3409,
        Level = LogLevel.Warning,
        Message = "Triage job {JobId} attempt {Attempt} ended without progress ({Reason}) after {RecoveriesUsed}/{MaxRecoveries} recoveries with {EvidenceCount} evidence items; the backend published an InsufficientEvidence report.")]
    private static partial void LogTerminated(
        ILogger logger, Guid jobId, int attempt, string reason, int recoveriesUsed, int maxRecoveries, int evidenceCount);

    [LoggerMessage(
        EventId = 3410,
        Level = LogLevel.Error,
        Message = "The backend no-progress report for triage job {JobId} attempt {Attempt} ({Reason}) was refused at publication; the attempt dead-letters.")]
    private static partial void LogTerminationRefused(ILogger logger, Guid jobId, int attempt, string reason);

    [LoggerMessage(
        EventId = 3411,
        Level = LogLevel.Warning,
        Message = "The backend no-progress report for triage job {JobId} attempt {Attempt} was published but its terminated budget event could not be recorded; carries the exception type {ExceptionType} only.")]
    private static partial void LogTerminationEventNotRecorded(ILogger logger, Guid jobId, int attempt, string exceptionType);
}
