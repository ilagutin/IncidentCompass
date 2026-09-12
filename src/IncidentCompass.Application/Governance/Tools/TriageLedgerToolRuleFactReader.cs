using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;

namespace IncidentCompass.Application.Governance.Tools;

internal sealed class TriageLedgerToolRuleFactReader(
    ITriageLedgerReader ledgerReader,
    TriageJob job) : IToolRuleFactReader
{
    public Task<int> CountAcceptedUsesAsync(
        string toolName,
        ToolRuleScope scope,
        CancellationToken cancellationToken) =>
        ledgerReader.CountPolicyDecisionsAsync(
            job, toolName, scope, TriageLedgerDecision.Allowed, cancellationToken);

    public Task<bool> HasSuccessfulToolResultAsync(
        string toolName,
        ToolRuleScope scope,
        CancellationToken cancellationToken) =>
        ledgerReader.HasSuccessfulToolResultAsync(job, toolName, scope, cancellationToken);
}
