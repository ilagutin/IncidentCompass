namespace IncidentCompass.Application.Remediation;

/// <summary>
/// One attempt to apply one candidate diff to a fresh copy of a named base.
/// </summary>
/// <param name="Target">Which monitored checkout to copy.</param>
/// <param name="BaseTreeIdentity">
/// The identity the diff was prepared against. The adapter must compare it against the copy it
/// materializes and refuse when they differ, before the diff is parsed or anything is written. This
/// is the whole of what binds an insert-only patch to a tree, so it is required rather than
/// optional: a caller with nothing to compare has no business applying a patch.
/// </param>
/// <param name="PatchText">
/// The candidate unified diff, exactly as it will be recorded and exactly as a reviewer would read
/// it. It is untrusted model text and is treated as a hostile document behind the port.
/// </param>
public sealed record RemediationApplyRequest(
    RemediationTarget Target,
    string BaseTreeIdentity,
    string PatchText);
