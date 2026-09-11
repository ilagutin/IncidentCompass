namespace IncidentCompass.Application.Remediation;

/// <summary>
/// The port a governed branch push reaches a code-hosting provider through: read one base, create one
/// branch, read one branch back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three operations, and deliberately no fourth.</b> There is no update, no delete, no merge and no
/// force. A caller cannot ask for one because no method takes one and no request field could carry
/// one, so "never force-push, never delete a branch, never merge" is a property of the contract rather
/// than a rule an adapter is trusted to keep. An adapter that wanted to do any of those would have to
/// grow a method, and a method has to be argued for.
/// </para>
/// <para>
/// <b>The owner, the repository, the remote and the base branch are not here.</b> They are host
/// configuration behind the implementation. Nothing above this port names them, so no payload, no
/// report, no ticket body and no model turn can select where a push lands; the most a caller can do is
/// choose the short branch name, and the only caller derives that from the origin report id.
/// </para>
/// <para>
/// <b>What the fingerprint is for.</b> It folds the whole binding - provider authority, repository and
/// base branch - into one digest that the approval contract records and compares again before
/// dispatch. Repointing a host at another repository or another base branch therefore turns a standing
/// approval into <c>adapter_binding_changed</c> rather than executing it somewhere nobody approved.
/// </para>
/// </remarks>
public interface ICodePublicationGateway
{
    /// <summary>Whether this host has an owner, a repository and a credential configured.</summary>
    bool IsConfigured { get; }

    /// <summary>The configured repository as <c>owner/name</c>, or null when nothing is configured.</summary>
    string? ConfiguredRepository { get; }

    /// <summary>The configured base branch a push builds on.</summary>
    string BaseBranch { get; }

    /// <summary>The binding digest a proposal freezes and a dispatch re-checks.</summary>
    string BindingFingerprint { get; }

    /// <summary>
    /// Reads a base: the commit, that commit's tree, and the tree's whole regular-file listing. A
    /// truncated or unsupported listing refuses rather than narrowing.
    /// </summary>
    /// <param name="pinnedCommitSha">
    /// <see langword="null" /> to read whatever the configured base branch points at now, which is
    /// what a proposal does. A commit name to read that exact commit, which is what a dispatch does:
    /// the approval named a parent, and the branch may have moved since without making that approval
    /// wrong. Re-reading the branch head at dispatch would silently rebase an approved change.
    /// </param>
    /// <param name="cancellationToken">Observed on every request the read makes.</param>
    Task<CodePublicationBaseResult> ReadBaseAsync(
        string? pinnedCommitSha,
        CancellationToken cancellationToken);

    /// <summary>Reads one branch reference without changing anything.</summary>
    Task<CodePublicationRefResult> ReadBranchAsync(string branchName, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the content-addressed objects the request describes and then creates the branch
    /// reference once. Object creation is idempotent by construction, so the reference create is the
    /// only operation whose second attempt would mean anything, and it is a compare-and-swap: a
    /// reference that already exists is never overwritten.
    /// </summary>
    Task<CodePublicationRefResult> PushAsync(
        CodePublicationPushRequest request,
        CancellationToken cancellationToken);
}
