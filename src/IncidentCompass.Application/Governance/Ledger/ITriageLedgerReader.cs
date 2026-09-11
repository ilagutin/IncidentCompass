using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;

namespace IncidentCompass.Application.Governance.Ledger;

public interface ITriageLedgerReader
{
    Task<TriageBudgetLedgerUsage> ReadBudgetUsageAsync(
        TriageJob job,
        CancellationToken cancellationToken);

    /// <remarks>
    /// The scope is the parsed <see cref="ToolRuleScope" /> rather than the configured string, so an
    /// adapter cannot invent a window for a value it does not recognize. See
    /// <see cref="ToolRuleScopes" />.
    /// </remarks>
    Task<int> CountPolicyDecisionsAsync(
        TriageJob job,
        string toolName,
        ToolRuleScope scope,
        TriageLedgerDecision decision,
        CancellationToken cancellationToken);

    /// <inheritdoc cref="CountPolicyDecisionsAsync" />
    Task<bool> HasSuccessfulToolResultAsync(
        TriageJob job,
        string toolName,
        ToolRuleScope scope,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the whole appended timeline of a fault, in append order, together with what each
    /// event's payload reference still resolves to. An adapter must never drop an event because the
    /// payload it references is gone: reconstruction of the event timeline is the point of this
    /// call, and <see cref="FaultLedgerEntry.PayloadState"/> is how a missing payload is reported.
    /// </summary>
    Task<IReadOnlyList<FaultLedgerEntry>> ReadByFaultIdAsync(
        Guid faultId,
        string tenantId,
        CancellationToken cancellationToken);
}
