using System.Text.Json;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;

namespace IncidentCompass.Application.Investigation.Jobs;

public sealed record InvestigationModelCallAccounting(
    Guid CallId,
    string? Role,
    ModelCallLedgerMetadata Metadata,
    int? ChargeTokens)
{
    public string PayloadRef => $"model-call:{CallId:N}";

    /// <summary>
    /// Whether this is the accounting of a call the provider answered rather than one that failed.
    /// <c>success</c> is the outcome <c>ModelCallLedgerAccountant.RecordSuccessAsync</c> writes; a
    /// failed call carries the failure outcome instead. Such accounting is only ever owed when the
    /// answer's own ledger append failed.
    /// </summary>
    public bool ProviderAnswered => string.Equals(Metadata.Outcome, "success", StringComparison.Ordinal);

    public IReadOnlyList<TriageLedgerAppendRequest> CreateLedgerRequests(TriageJob job)
    {
        var modelCall = new TriageLedgerAppendRequest(
            job.FaultId,
            job.Id,
            job.Attempt,
            TriageLedgerEventType.ModelCall,
            Role,
            ToolName: null,
            JsonSerializer.Serialize(Metadata),
            Decision: null,
            DecisionReason: null,
            PayloadRef: PayloadRef,
            ConfigHash: job.ConfigHash);
        if (ChargeTokens is not { } chargeTokens)
        {
            return [modelCall];
        }

        return
        [
            modelCall,
            new TriageLedgerAppendRequest(
                job.FaultId,
                job.Id,
                job.Attempt,
                TriageLedgerEventType.BudgetEvent,
                Role: null,
                ToolName: null,
                Rationale: "model_call_charged: charged model tokens to the attempt budget.",
                Decision: null,
                DecisionReason: null,
                PayloadRef: PayloadRef,
                ConfigHash: job.ConfigHash,
                ToolStatus: null,
                TokensDelta: chargeTokens,
                WorkersDelta: null)
        ];
    }
}
