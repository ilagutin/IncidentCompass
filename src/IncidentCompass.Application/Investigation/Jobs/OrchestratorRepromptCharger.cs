using IncidentCompass.Application.Core.Text;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Domain.Incidents;
using Microsoft.Extensions.Logging;

namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// Charges orchestrator correction turns against <c>Orchestrator.Budget.MaxReprompts</c>. It is
/// constructed by the processor with the processor's own logger instance, so events 3401 and 3403 keep
/// their category.
/// </summary>
internal sealed partial class OrchestratorRepromptCharger(TriageLedgerAppender ledgerAppender, ILogger logger)
{
    /// <summary>
    /// Charges one reprompt against the configured allowance and returns the new count, or throws when
    /// the allowance is already spent. Returning the count keeps the counter an ordinary local owned by
    /// the loop instead of shared mutable state written through a <c>ref</c> parameter.
    /// </summary>
    /// <remarks>
    /// A spent allowance is a bounded-run limit like any other, so it leaves under
    /// <see cref="TriageBudgetExhaustedException.OrchestratorRepromptLimitReachedCode"/>: the attempt
    /// dead-letters under its own durable code instead of consuming a retry under the generic
    /// attempt-failure code. One code covers every call site because the condition is one condition -
    /// the allowance is spent - while the cause of each individual correction turn is already durable
    /// per reprompt in log event 3401 and the matching <c>orchestrator_reprompt:</c> ledger
    /// <c>BudgetEvent</c>. The exhausting turn's own cause, which never becomes a reprompt, is logged
    /// here as event 3403 so nothing about why the allowance ran out depends on the error code alone.
    /// </remarks>
    public async Task<int> ChargeOrThrowAsync(
        TriageJob job,
        TriageConfiguration configuration,
        int reprompts,
        string repromptReason,
        string validationDiagnostic,
        string exhaustedMessage,
        CancellationToken cancellationToken,
        Exception? innerException = null)
    {
        // The log site and the ledger site describe the same turn, so they are bounded once, by the
        // same constant, before either is written. The appender then charges the rationale prefix
        // against that same bound so the prefix cannot displace part of the diagnostic.
        var safeDiagnostic = TextTruncator.Truncate(
            validationDiagnostic,
            TriageLedgerAppender.MaxRepromptRationaleLength);
        if (reprompts >= configuration.Orchestrator.Budget.MaxReprompts)
        {
            LogOrchestratorRepromptLimitReached(
                logger,
                job.Id,
                job.Attempt,
                repromptReason,
                safeDiagnostic,
                configuration.Orchestrator.Budget.MaxReprompts);
            throw new TriageBudgetExhaustedException(
                TriageBudgetExhaustedException.OrchestratorRepromptLimitReachedCode,
                exhaustedMessage,
                innerException);
        }

        var chargedReprompts = reprompts + 1;
        LogOrchestratorReprompted(
            logger,
            job.Id,
            job.Attempt,
            repromptReason,
            safeDiagnostic,
            chargedReprompts,
            configuration.Orchestrator.Budget.MaxReprompts);
        await ledgerAppender.AppendRepromptBudgetEventAsync(
            job,
            "orchestrator",
            GovernedTriageInvestigationProcessor.OrchestratorRepromptRationalePrefix + repromptReason + ": ",
            safeDiagnostic,
            cancellationToken);
        return chargedReprompts;
    }

    [LoggerMessage(
        EventId = 3401,
        Level = LogLevel.Information,
        Message = "Orchestrator for triage job {JobId} attempt {Attempt} was reprompted because of {RepromptReason} ({Reprompts}/{MaxReprompts}): {ValidationDiagnostic}")]
    private static partial void LogOrchestratorReprompted(
        ILogger logger,
        Guid jobId,
        int attempt,
        string repromptReason,
        string validationDiagnostic,
        int reprompts,
        int maxReprompts);

    [LoggerMessage(
        EventId = 3403,
        Level = LogLevel.Warning,
        Message = "Orchestrator for triage job {JobId} attempt {Attempt} spent its bounded reprompt allowance ({MaxReprompts}) and could not correct {RepromptReason}: {ValidationDiagnostic}")]
    private static partial void LogOrchestratorRepromptLimitReached(
        ILogger logger,
        Guid jobId,
        int attempt,
        string repromptReason,
        string validationDiagnostic,
        int maxReprompts);
}
