namespace IncidentCompass.Application.Remediation;

/// <summary>
/// Everything a <c>pr_create</c> proposal freezes, in the one shape written into the approval payload
/// and read back out of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What an approval over these bytes covers.</b> Where the pull request lands
/// (<paramref name="Repository" />, <paramref name="BaseBranch" />), what it proposes
/// (<paramref name="HeadBranch" /> at exactly <paramref name="HeadCommitSha" />), what that change is
/// against (<paramref name="BaseCommitSha" />, <paramref name="BaseTreeIdentity" />,
/// <paramref name="ResultTreeIdentity" />), what was proved about it
/// (<paramref name="CorrespondenceDigest" />, <paramref name="ProvedPathCount" />,
/// <paramref name="ExcludedPathCount" />), what it says about itself
/// (<paramref name="ReportConfidence" />, <paramref name="IssueNumber" />), and which earlier approved
/// action it continues (<paramref name="PredecessorActionId" />,
/// <paramref name="PredecessorResultSha256" />).
/// </para>
/// <para>
/// <b>The head is bound to a commit and not only to a name.</b> The branch name is derived from the
/// origin report and so is stable, but a name is not a change. <paramref name="HeadCommitSha" /> is the
/// commit the earlier push actually confirmed, taken from that action's own compact audit projection,
/// and the dispatch reads the reference and refuses unless it still points there. A person therefore
/// approves publishing one commit, not whatever that branch holds when the dispatch runs.
/// </para>
/// <para>
/// <b>The title and the body are here because they are public.</b> They are frozen so that what a
/// person approved is byte-for-byte what a stranger reads, and both are re-derived at dispatch and
/// refused on any difference, so a row edited by hand cannot put a sentence on a public page. Neither
/// carries a repository, a path, a host, a credential, a line of source, a prompt or any other free
/// text: every value in them is a report identifier, an issue number, a closed-vocabulary confidence,
/// a count or a hex digest.
/// </para>
/// <para>
/// <b>The repository is here to be checked, not to be chosen.</b> It is host configuration copied into
/// the frozen bytes so that a person approves a named repository and a dispatch can refuse a host that
/// has been repointed since. Nothing reads it to decide where to send anything.
/// </para>
/// </remarks>
internal sealed record PullRequestPayload(
    Guid OriginReportId,
    string ServiceName,
    string Release,
    string Repository,
    string BaseBranch,
    string HeadBranch,
    string HeadCommitSha,
    string BaseCommitSha,
    string BaseTreeIdentity,
    string ResultTreeIdentity,
    int FilesChanged,
    int ProvedPathCount,
    int ExcludedPathCount,
    string CorrespondenceDigest,
    string ReportConfidence,
    int IssueNumber,
    Guid PredecessorActionId,
    string PredecessorResultSha256);
