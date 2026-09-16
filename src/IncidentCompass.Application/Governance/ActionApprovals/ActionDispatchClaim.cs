namespace IncidentCompass.Application.Governance.ActionApprovals;

public sealed record ActionDispatchClaim(ActionApprovalRecord Action, Guid Fence)
{
    /// <summary>
    /// The adapter limit the claim deadline was computed from, resolved for this tool by
    /// <see cref="IApprovedActionDispatcher.TryClaimAsync"/> before claiming. Dispatch runs the
    /// adapter under exactly this value, so the adapter deadline and <c>dispatch_deadline_at</c> come
    /// from the same limit. The repository cannot resolve it, so a claim returned straight from
    /// <see cref="IActionDispatchRepository"/> carries none and dispatch refuses it.
    /// </summary>
    public TimeSpan? AdapterTimeout { get; init; }
}
