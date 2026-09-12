using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Infrastructure.Tickets;

namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// The adapter binding a frozen <c>branch_push</c> approval is tied to: which provider, which
/// repository and which base branch this host is pointed at, stated as a digest.
/// </summary>
/// <remarks>
/// The approval contract records it on the action row and compares it again before dispatch, so an
/// approval taken against one wiring cannot execute against another. The base branch is part of it for
/// the same reason the repository is: rebasing a standing approval onto a different line of
/// development produces a change nobody reviewed, and a logical target alone says nothing about which
/// branch, because <c>code:configured-repository</c> stays the same string when an operator edits one.
/// </remarks>
internal static class GitHubCodePublicationBinding
{
    private const string ToolKind = "github-git-data";
    private const char FieldSeparator = (char)0x1e;
    private const string Unconfigured = "unconfigured";

    public static string ComputeFingerprint(
        GitHubIssuesOptions repositoryOptions,
        GitHubCodePublicationOptions publicationOptions) =>
        ExternalActionBinding.ComputeFingerprint(
            ToolKind,
            BranchPushToolDescriptor.LogicalTargetId,
            GitHubIssuesTicketSearch.Authority.AbsoluteUri,
            string.Concat(
                repositoryOptions.ConfiguredRepository ?? Unconfigured,
                FieldSeparator,
                publicationOptions.BaseBranch ?? Unconfigured));
}
