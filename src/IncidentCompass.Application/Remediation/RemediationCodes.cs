namespace IncidentCompass.Application.Remediation;

/// <summary>
/// The Application-owned half of the closed outcome vocabulary a remediation pass reports.
/// </summary>
/// <remarks>
/// <para>
/// The vocabulary has two families and both reach the same fields. These <c>remediation_*</c> codes
/// describe what the pass itself decided: what was not configured, what the model answered, and
/// whether the base still exists. The adapter behind <see cref="IRemediationWorkspace" /> also
/// returns its own <c>source_workspace_*</c> and <c>source_patch_*</c> codes unchanged, because a
/// pass that translated them would either lose the reason or invent a second name for it. Both
/// families are safe to log and persist: every one of them is a fixed string, and none carries a
/// path, a line number, a byte of a diff or a byte of a file.
/// </para>
/// <para>
/// Nothing here is a status the model chooses. A code is what the backend concluded.
/// </para>
/// </remarks>
public static class RemediationCodes
{
    /// <summary>A base tree was named and a change can be prepared against it.</summary>
    public const string BaseIdentified = "remediation_base_identified";

    /// <summary>The whole diff applied and the resulting tree was identified.</summary>
    public const string Applied = "remediation_applied";

    /// <summary>A diff was produced, applied against its stated base and recorded.</summary>
    public const string Produced = "remediation_diff_produced";

    /// <summary>
    /// No host wiring answers for this target: no workspace root is configured, or the service and
    /// release name no configured monitored root. Remediation is off unless an operator turns it on.
    /// </summary>
    public const string NotConfigured = "remediation_not_configured";

    /// <summary>
    /// The job's configuration snapshot names no current release for the faulting service, so there
    /// is no backend-selected checkout to prepare a change against.
    /// </summary>
    public const string ReleaseUnavailable = "remediation_release_unavailable";

    /// <summary>
    /// The named route is not in the job's configuration snapshot. Load-time validation refuses a
    /// configuration like that, but a rehydrated snapshot must fail closed rather than throw.
    /// </summary>
    public const string RouteMissing = "remediation_route_missing";

    /// <summary>
    /// The report cites no source evidence, so a diff would be written against files the
    /// investigation never read. Refused rather than attempted.
    /// </summary>
    public const string SourceEvidenceMissing = "remediation_source_evidence_missing";

    /// <summary>
    /// The model answered with something that is not a unified diff: prose, an apology, a fenced
    /// block that holds no diff, or a diff with commentary around it. Correctable by the model.
    /// </summary>
    public const string AnswerNotAPatch = "remediation_answer_not_a_patch";

    /// <summary>
    /// The workspace materialized for the apply is not the base the diff was prepared against. The
    /// monitored checkout moved, and the diff is now about a tree that no longer exists. Not
    /// correctable by the model: nothing it can write makes the old base come back.
    /// </summary>
    public const string BaseMismatch = "remediation_base_mismatch";
}
