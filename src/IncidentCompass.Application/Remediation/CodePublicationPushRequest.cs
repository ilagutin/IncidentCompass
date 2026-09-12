namespace IncidentCompass.Application.Remediation;

/// <summary>
/// One push: build the base commit's tree with these overlays, commit it on top of that exact commit,
/// and create one new branch reference at the result.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no force, no delete and no merge here, and no way to express one.</b> The request names
/// a branch to create and a parent to build on, and nothing else. An adapter behind this contract has
/// no field it could read to update a reference, remove one, or merge anything, so refusing those is
/// not a rule an implementation is trusted to follow but a shape it cannot describe.
/// </para>
/// <para>
/// <b>Every field is frozen in the approval.</b> The branch name is derived from the origin report,
/// the base commit came from the read that proved the correspondence, the overlays are the approved
/// diff's own output, and the timestamp was chosen when the proposal was created rather than when the
/// dispatch runs. Pinning the timestamp is what makes the commit content-addressable to the same id on
/// every attempt, which is what leaves the reference create as the only operation that can happen once.
/// </para>
/// </remarks>
/// <param name="BranchName">
/// The short branch name to create, without the <c>refs/heads/</c> prefix. Backend-derived.
/// </param>
/// <param name="BaseCommitSha">The parent commit, proved to be the approved base tree.</param>
/// <param name="BaseTreeSha">That commit's tree, which the overlays are laid over.</param>
/// <param name="Entries">The overlays. Bounded by the approved diff's own file-section bound.</param>
/// <param name="CommitMessage">Backend-composed. It carries no model text.</param>
/// <param name="CommitTimestampUtc">
/// The instant pinned into the approval, used as both author and committer date.
/// </param>
public sealed record CodePublicationPushRequest(
    string BranchName,
    string BaseCommitSha,
    string BaseTreeSha,
    IReadOnlyList<CodePublicationTreeEntry> Entries,
    string CommitMessage,
    DateTimeOffset CommitTimestampUtc);
