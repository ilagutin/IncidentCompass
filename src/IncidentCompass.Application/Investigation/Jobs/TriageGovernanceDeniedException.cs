namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// Raised when backend governance refuses to proceed during an investigation attempt: a worker tool
/// call it denies, or a configuration reference the governed loop cannot resolve. Policy grants, tool
/// rules and the job's config hash are attempt-invariant, so replaying the attempt would be denied again;
/// <see cref="TriageJobRunner"/> dead-letters instead of spending the retry budget. The denial itself
/// is already recorded as a durable policy decision in the triage ledger before this is thrown.
/// <see cref="ErrorCode"/> lands in the job's <c>last_error_code</c> column.
/// </summary>
internal sealed class TriageGovernanceDeniedException(string errorCode, string message) : Exception(message)
{
    public const string WorkerToolDeniedCode = "triage_governance_worker_tool_denied";

    public const string WorkerToolValidationFailedCode = "triage_governance_worker_tool_validation_failed";

    /// <summary>
    /// The rehydrated configuration names an orchestrator route it does not contain. The job is pinned
    /// to that config hash, so every later attempt would resolve the same missing route.
    /// </summary>
    public const string OrchestratorRouteMissingCode = "triage_governance_orchestrator_route_missing";

    /// <summary>
    /// The rehydrated configuration names a route on a role it does not contain. Kept distinct from
    /// <see cref="OrchestratorRouteMissingCode"/> because the operator fix is in a different place:
    /// this points at one <c>roles.&lt;name&gt;.routeId</c> entry, not at the orchestrator root.
    /// </summary>
    public const string WorkerRouteMissingCode = "triage_governance_worker_route_missing";

    /// <summary>
    /// The immediate <c>memory_search</c> tool names an embedding route that is absent from the
    /// rehydrated configuration. The attempt must stop before embedding or memory retrieval begins.
    /// </summary>
    public const string MemorySearchRouteMissingCode = "triage_governance_memory_search_route_missing";

    public string ErrorCode { get; } = errorCode;
}
