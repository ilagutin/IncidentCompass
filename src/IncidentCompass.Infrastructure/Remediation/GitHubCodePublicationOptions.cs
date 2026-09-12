namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// The only thing a host says about code publication that it does not already say about issues: which
/// branch a change is based on.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no owner, repository or token here, and that is the decision.</b> The code binding is
/// the issue binding: the same owner, the same repository and the same credential that
/// <c>IncidentCompass:Tickets:GitHub</c> already carries. A second repository setting and a second
/// token would double the secret inventory, create a path where a credential with write access to
/// source is configured without anyone reviewing it beside the one they thought they were configuring,
/// and let a deployment drift into pushing branches to a repository nobody associated with this
/// product's incidents. Reusing one binding means the repository that receives a branch is the
/// repository an operator already named, and the credential that creates it is the credential they
/// already know about. What changes is the scope that credential needs, and that change is deliberate:
/// it is made by an operator who is turning <c>branch_push</c> on, which is off in the shipped
/// configuration.
/// </para>
/// <para>
/// <b>There is no default base branch.</b> An unset value means code publication is not configured
/// here, and every call refuses. A default such as the usual primary-branch name would be this file
/// quietly choosing what changes are based on, on the first host that ever configured a repository for
/// issues.
/// </para>
/// </remarks>
public sealed class GitHubCodePublicationOptions
{
    public const string SectionName = "IncidentCompass:Publication:GitHub";

    /// <summary>The branch a push reads its base commit from. Never written to.</summary>
    public string? BaseBranch { get; init; }

    /// <summary>
    /// Per-call deadline. Larger than the issue adapter's default because a push makes several small
    /// requests where an issue create makes one, and each of them is still bounded.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 20;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseBranch);
}
