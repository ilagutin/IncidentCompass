using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;

namespace IncidentCompass.Application.Investigation.Jobs;

internal sealed class TriageLedgerAppender(ITriageLedgerWriter ledgerWriter)
{
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
}
