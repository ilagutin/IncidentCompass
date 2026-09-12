using System.Text;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Infrastructure.SourceContext;
using IncidentCompass.Infrastructure.Tickets;

namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// The two pull-request calls a governed publication makes: ask which pull request answers for one
/// head, and open one.
/// </summary>
/// <remarks>
/// <para>
/// It sits beside the gateway rather than inside it because the gateway already carries the whole Git
/// Data half - blobs, trees, commits and references - and one type holding both would be the place
/// every later addition went. The gateway keeps the decisions that need the host binding: whether
/// anything is configured, which base branch is in force, and whether the head is still the commit the
/// approval named. This type keeps the two requests and the parsing of their answers.
/// </para>
/// <para>
/// <b>There is no third method here and no way to grow one quietly.</b> Merging, enabling an automatic
/// merge, editing a pull request and changing a repository setting each need a request factory that
/// does not exist, and the port above has no method that would call one.
/// </para>
/// </remarks>
internal sealed class GitHubPullRequestOperations(
    GitHubIssuesOptions options,
    GitHubGitDataSender sender,
    int maximumResponseBytes)
{
    /// <summary>Maximum UTF-8 bytes in a title, the bound the issue adapter already uses.</summary>
    public const int MaximumTitleBytes = 256;

    /// <summary>Maximum UTF-8 bytes in a body, the bound the issue adapter already uses.</summary>
    public const int MaximumBodyBytes = 4096;

    /// <summary>
    /// Whether a request carries text this adapter will publish. It is checked here even though the
    /// payload factory already bounded it, because this is the last place before bytes become a page.
    /// </summary>
    public static bool IsPublishable(CodePublicationPullRequestRequest request) =>
        IsWithinBound(request.Title, MaximumTitleBytes) &&
        IsWithinBound(request.Body, MaximumBodyBytes);

    /// <summary>
    /// Reads whatever pull request answers for one head against one base. A null expected commit is
    /// the reconciliation's question; every other caller passes the commit the approval froze.
    /// </summary>
    public async Task<CodePublicationPullRequestResult> FindAsync(
        string headBranch,
        string? expectedHeadCommitSha,
        string baseBranch,
        CancellationToken cancellationToken)
    {
        if (!GitReferenceName.IsValid(headBranch) ||
            (expectedHeadCommitSha is not null && !GitBlobIdentity.IsValid(expectedHeadCommitSha)))
        {
            return CodePublicationPullRequestResult.Refused(CodePublicationCodes.RequestInvalid);
        }

        var (document, code) = await sender.SendAsync(
            () => GitHubGitDataRequests.ReadPullRequests(options, headBranch, baseBranch),
            maximumResponseBytes,
            cancellationToken);
        using (document)
        {
            if (document is null)
            {
                return CodePublicationPullRequestResult.Refused(code!);
            }

            var (number, outcome) = GitHubPullRequestParser.ReadListing(
                document.RootElement, headBranch, expectedHeadCommitSha, baseBranch);
            return new CodePublicationPullRequestResult(outcome, number);
        }
    }

    /// <summary>
    /// Sends the one request that creates something. A create whose answer never arrived, or whose
    /// answer this adapter cannot match to what it asked for, is an unknown outcome rather than a
    /// refusal: the provider may well have created it, and the listing read above settles that later.
    /// </summary>
    public async Task<CodePublicationPullRequestResult> OpenAsync(
        CodePublicationPullRequestRequest request,
        string baseBranch,
        CancellationToken cancellationToken)
    {
        var (document, code) = await sender.SendAsync(
            () => GitHubGitDataRequests.CreatePullRequest(options, request, baseBranch),
            maximumResponseBytes,
            cancellationToken,
            unknownOnFault: true);
        using (document)
        {
            if (document is null)
            {
                return CodePublicationPullRequestResult.Refused(code!);
            }

            var number = GitHubPullRequestParser.ReadOne(
                document.RootElement, request.HeadBranch, request.HeadCommitSha, baseBranch).Number;
            return number is null
                ? CodePublicationPullRequestResult.Refused(CodePublicationCodes.OutcomeUnknown)
                : new CodePublicationPullRequestResult(CodePublicationCodes.PullRequestOpened, number);
        }
    }

    private static bool IsWithinBound(string value, int maximumBytes) =>
        !string.IsNullOrWhiteSpace(value) && Encoding.UTF8.GetByteCount(value) <= maximumBytes;
}
