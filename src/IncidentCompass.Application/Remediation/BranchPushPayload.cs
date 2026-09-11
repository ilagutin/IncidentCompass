namespace IncidentCompass.Application.Remediation;

/// <summary>
/// Everything a <c>branch_push</c> proposal freezes, in the one shape written into the approval
/// payload and read back out of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What an approval over these bytes covers.</b> Where the change lands
/// (<paramref name="Repository" />, <paramref name="BaseBranch" />, <paramref name="BranchName" />),
/// what it lands on (<paramref name="BaseCommitSha" /> and <paramref name="BaseTreeSha" />), what it
/// is (<paramref name="PatchText" /> against <paramref name="BaseTreeIdentity" />, producing
/// <paramref name="ResultTreeIdentity" />), what was proved about the base
/// (<paramref name="CorrespondenceDigest" />, <paramref name="ProvedPathCount" />,
/// <paramref name="ExcludedPathCount" />), which earlier approved action it continues
/// (<paramref name="PredecessorActionId" />, <paramref name="PredecessorResultSha256" />), and the
/// instant pinned into the commit (<paramref name="CommitTimestampUtc" />).
/// </para>
/// <para>
/// <b>Why the resulting commit is bound by determination rather than by name.</b> Every input to the
/// commit is here and every one of them is hashed, and the commit is content-addressed, so these bytes
/// determine exactly one commit id. Naming that id in the payload would mean either building git's
/// tree and commit objects inside this product - a second implementation of a format whose failure
/// mode is a feature that refuses forever - or creating the objects in the remote repository while the
/// proposal is still waiting for a person, which is an external write before an approval. Neither is
/// worth the literal value, so the dispatch proves the commit instead: it refuses unless the commit
/// the provider built has this exact parent and this exact base tree, before the reference is touched,
/// and the commit id is recorded on the action row once it exists.
/// </para>
/// <para>
/// <b>The repository is here to be checked, not to be chosen.</b> It is host configuration copied into
/// the frozen bytes so that a person approves a named repository and a dispatch can refuse a host that
/// has been repointed since. Nothing reads it to decide where to send anything; the adapter uses its
/// own options for that, and the binding fingerprint fails the dispatch first.
/// </para>
/// </remarks>
internal sealed record BranchPushPayload(
    Guid OriginReportId,
    string ServiceName,
    string Release,
    string Repository,
    string BaseBranch,
    string BranchName,
    string BaseCommitSha,
    string BaseTreeSha,
    string BaseTreeIdentity,
    string ResultTreeIdentity,
    int FilesChanged,
    int PatchBytes,
    string PatchText,
    string CorrespondenceDigest,
    int ProvedPathCount,
    int ExcludedPathCount,
    Guid PredecessorActionId,
    string PredecessorResultSha256,
    DateTimeOffset CommitTimestampUtc);
