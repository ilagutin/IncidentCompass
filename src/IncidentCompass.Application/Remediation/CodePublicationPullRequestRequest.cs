namespace IncidentCompass.Application.Remediation;

/// <summary>
/// One pull request: open it from this head, with this exact title and this exact body.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no base here, and that is deliberate.</b> The branch a pull request targets is host
/// configuration the adapter reads for itself. A caller therefore cannot express "merge into that
/// branch instead", because there is no field in which to say it, and the one place the base is
/// decided is the same option the push already reads its base commit from.
/// </para>
/// <para>
/// <b>There is no merge, no auto-merge and no repository setting here either.</b> The record names a
/// head, a title and a body. Nothing in it can ask for a merge, enable one, mark a pull request
/// mergeable, or change anything about the repository, so refusing those is a shape the request cannot
/// describe rather than a rule an implementation is trusted to keep.
/// </para>
/// </remarks>
/// <param name="HeadBranch">
/// The short branch name to open from, without the <c>refs/heads/</c> prefix. Backend-derived from the
/// origin report, exactly as the push that created it was.
/// </param>
/// <param name="HeadCommitSha">
/// The commit an earlier approved push confirmed at that branch. The adapter refuses unless the branch
/// still points at it, so a pull request is never opened over a reference someone moved after approval.
/// </param>
/// <param name="Title">Backend-composed and bounded. It carries no model text.</param>
/// <param name="Body">Backend-composed and bounded. It carries no model text.</param>
public sealed record CodePublicationPullRequestRequest(
    string HeadBranch,
    string HeadCommitSha,
    string Title,
    string Body);
