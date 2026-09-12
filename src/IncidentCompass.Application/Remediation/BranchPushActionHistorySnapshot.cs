namespace IncidentCompass.Application.Remediation;

/// <summary>
/// What earlier branch-push actions for the same incident and binding already did.
/// </summary>
/// <param name="ConfirmedCanonicalResult">
/// The canonical result of an earlier executed push, or <see langword="null" /> when there is none.
/// Returning it means this dispatch answers with the earlier outcome instead of creating anything.
/// </param>
/// <param name="ConfirmedSummary">That earlier execution's summary.</param>
/// <param name="ConfirmedCommitSha">
/// The commit the earlier execution recorded, taken from the compact audit projection on its own row
/// rather than by parsing its result. That projection exists precisely so this question is a column
/// read.
/// </param>
/// <param name="HasPendingAction">
/// Whether another push for this incident is still requested or approved. Two live proposals for one
/// branch is a state a person has to resolve, not one an adapter should race.
/// </param>
/// <param name="HasOutcomeUnknown">
/// Whether an earlier push ended without knowing whether the reference was created.
/// </param>
/// <param name="HistoryLimitExceeded">
/// Whether there are more sibling actions than this reader will look at. It refuses rather than
/// deciding from a prefix of the history.
/// </param>
public sealed record BranchPushActionHistorySnapshot(
    byte[]? ConfirmedCanonicalResult,
    string? ConfirmedSummary,
    string? ConfirmedCommitSha,
    bool HasPendingAction,
    bool HasOutcomeUnknown,
    bool HistoryLimitExceeded);
