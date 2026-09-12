using IncidentCompass.Application.Core.Text;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;

namespace IncidentCompass.Application.Investigation.Jobs;

internal sealed class TriageLedgerAppender(ITriageLedgerWriter ledgerWriter)
{
    /// <summary>
    /// The one bound on a reprompt <c>BudgetEvent</c> rationale. It lives on the appender because the
    /// appender owns the field: a caller that truncated its own diagnostic and then prepended a prefix
    /// would push the prefix's length back off the end of the diagnostic here, so the prefix budget is
    /// charged once, in <see cref="AppendRepromptBudgetEventAsync" />, against the same bound.
    /// </summary>
    internal const int MaxRepromptRationaleLength = 1000;

    public Task AppendAsync(
        TriageJob job,
        TriageLedgerEventType eventType,
        string? role,
        string? toolName,
        string? rationale,
        string? payloadRef,
        CancellationToken cancellationToken)
    {
        return AppendCoreAsync(
            job,
            eventType,
            role,
            toolName,
            rationale,
            decision: null,
            decisionReason: null,
            payloadRef,
            toolStatus: null,
            tokensDelta: null,
            workersDelta: null,
            cancellationToken);
    }

    public Task AppendToolResultAsync(
        TriageJob job,
        string role,
        string toolName,
        TriageLedgerToolStatus status,
        string rationale,
        string? payloadRef,
        CancellationToken cancellationToken)
    {
        return AppendCoreAsync(
            job,
            TriageLedgerEventType.ToolResult,
            role,
            toolName,
            rationale,
            decision: null,
            decisionReason: null,
            payloadRef,
            status,
            tokensDelta: null,
            workersDelta: null,
            cancellationToken);
    }

    public Task AppendPolicyDecisionAsync(
        TriageJob job,
        string role,
        string toolName,
        TriageLedgerDecision decision,
        string reason,
        CancellationToken cancellationToken)
    {
        return AppendCoreAsync(
            job,
            TriageLedgerEventType.PolicyDecision,
            role,
            toolName,
            rationale: null,
            decision,
            reason,
            payloadRef: null,
            toolStatus: null,
            tokensDelta: null,
            workersDelta: null,
            cancellationToken);
    }

    public Task AppendBudgetEventAsync(
        TriageJob job,
        string? rationale,
        int? tokensDelta,
        int? workersDelta,
        CancellationToken cancellationToken)
    {
        return AppendCoreAsync(
            job,
            TriageLedgerEventType.BudgetEvent,
            role: null,
            toolName: null,
            rationale,
            decision: null,
            decisionReason: null,
            payloadRef: null,
            toolStatus: null,
            tokensDelta,
            workersDelta,
            cancellationToken);
    }

    /// <summary>
    /// Appends a reprompt <c>BudgetEvent</c> whose rationale is <paramref name="rationalePrefix" />
    /// followed by <paramref name="diagnostic" />. The prefix is a short, closed classification the
    /// caller owns and the diagnostic is the variable-length part, so only the diagnostic is cut, and
    /// it is cut to whatever the prefix leaves of <see cref="MaxRepromptRationaleLength" />. The prefix
    /// is therefore always readable in the ledger, and the diagnostic is never amputated twice. A
    /// prefix that is not short leaves no room for the diagnostic and is itself cut to the bound.
    /// </summary>
    public Task AppendRepromptBudgetEventAsync(
        TriageJob job,
        string role,
        string rationalePrefix,
        string diagnostic,
        CancellationToken cancellationToken)
    {
        return AppendCoreAsync(
            job,
            TriageLedgerEventType.BudgetEvent,
            role,
            toolName: null,
            ComposeRepromptRationale(rationalePrefix, diagnostic),
            decision: null,
            decisionReason: null,
            payloadRef: null,
            toolStatus: null,
            tokensDelta: null,
            workersDelta: null,
            cancellationToken);
    }

    public async Task AppendModelCallAccountingAsync(
        TriageJob job,
        InvestigationModelCallAccounting accounting,
        CancellationToken cancellationToken)
    {
        try
        {
            await ledgerWriter.AppendBatchAsync(
                accounting.CreateLedgerRequests(job),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvestigationModelCallFailureException(accounting, exception);
        }
    }

    private async Task AppendCoreAsync(
        TriageJob job,
        TriageLedgerEventType eventType,
        string? role,
        string? toolName,
        string? rationale,
        TriageLedgerDecision? decision,
        string? decisionReason,
        string? payloadRef,
        TriageLedgerToolStatus? toolStatus,
        int? tokensDelta,
        int? workersDelta,
        CancellationToken cancellationToken)
    {
        await ledgerWriter.AppendAsync(
            new TriageLedgerAppendRequest(
                job.FaultId,
                job.Id,
                job.Attempt,
                eventType,
                role,
                toolName,
                rationale,
                decision,
                decisionReason,
                payloadRef,
                job.ConfigHash,
                toolStatus,
                tokensDelta,
                workersDelta),
            cancellationToken);
    }

    /// <summary>
    /// Composes the reprompt rationale under <see cref="MaxRepromptRationaleLength" />: the diagnostic
    /// is cut to whatever the prefix leaves of the bound.
    /// </summary>
    /// <remarks>
    /// A prefix long enough to fill the bound on its own leaves nothing for the diagnostic, so the
    /// prefix is cut instead of being subtracted from the bound and passed on as a non-positive
    /// length. The contract only asks callers for a short prefix, and a ledger append that threw
    /// because a future caller composed a longer one would turn a bounded field into a failed charge.
    /// </remarks>
    private static string ComposeRepromptRationale(string rationalePrefix, string diagnostic)
    {
        var remainingLength = MaxRepromptRationaleLength - rationalePrefix.Length;
        return remainingLength > 0
            ? rationalePrefix + TextTruncator.Truncate(diagnostic, remainingLength)
            : TextTruncator.Truncate(rationalePrefix, MaxRepromptRationaleLength);
    }
}
