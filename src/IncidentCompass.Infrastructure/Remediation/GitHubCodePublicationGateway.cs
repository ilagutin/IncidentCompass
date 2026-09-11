using System.Net;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Infrastructure.SourceContext;
using IncidentCompass.Infrastructure.Tickets;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// The GitHub adapter behind <see cref="ICodePublicationGateway" />: reads one base, builds
/// content-addressed objects, creates one branch reference, and opens one pull request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only two operations here can happen twice and mean something.</b> Blobs, trees and commits are
/// named by their content, so creating one that already exists returns the same name and changes
/// nothing; the object half of a push is idempotent by construction, which is why the commit's author
/// and committer dates are pinned by the approval rather than taken from a clock. What is left is the
/// reference create, a compare-and-swap the provider refuses when the name is taken, and the
/// pull-request create, which is preceded unconditionally by the read that asks the same question.
/// </para>
/// <para>
/// <b>The bounds are the issue adapter's.</b> No redirects, an infinite client timeout with a
/// per-call linked deadline instead, and every response body read through a bound. The tree read's
/// bound is larger because a repository listing is, and it refuses rather than truncating.
/// </para>
/// </remarks>
public sealed class GitHubCodePublicationGateway : ICodePublicationGateway, IDisposable
{
    /// <summary>Enough for a recursive listing of a large repository; refused, never truncated.</summary>
    private const int MaximumTreeResponseBytes = 8 * 1024 * 1024;

    private const int MaximumResponseBytes = 128 * 1024;
    private const int MaximumEntries = 64;

    private readonly GitHubIssuesOptions repositoryOptions;
    private readonly GitHubCodePublicationOptions publicationOptions;
    private readonly GitHubGitDataSender sender;
    private readonly GitHubPullRequestOperations pullRequests;
    private readonly HttpClient client;

    public GitHubCodePublicationGateway(
        IOptions<GitHubIssuesOptions> repositoryOptions,
        IOptions<GitHubCodePublicationOptions> publicationOptions,
        HttpMessageHandler? handler = null)
    {
        this.repositoryOptions = repositoryOptions.Value;
        this.publicationOptions = publicationOptions.Value;
        client = new HttpClient(handler ?? GitHubIssuesHttpMessageHandlerFactory.Create(), disposeHandler: true)
        {
            BaseAddress = GitHubIssuesTicketSearch.Authority,
            Timeout = Timeout.InfiniteTimeSpan
        };
        sender = new GitHubGitDataSender(client, this.publicationOptions.TimeoutSeconds);
        pullRequests = new GitHubPullRequestOperations(
            this.repositoryOptions, sender, MaximumResponseBytes);
        BindingFingerprint = GitHubCodePublicationBinding.ComputeFingerprint(
            this.repositoryOptions, this.publicationOptions);
    }

    public bool IsConfigured =>
        repositoryOptions.IsConfigured && publicationOptions.IsConfigured &&
        GitReferenceName.IsValid(publicationOptions.BaseBranch);

    public string? ConfiguredRepository => repositoryOptions.ConfiguredRepository;

    public string BaseBranch => publicationOptions.BaseBranch ?? string.Empty;

    public string BindingFingerprint { get; }

    public void Dispose() => client.Dispose();

    public async Task<CodePublicationBaseResult> ReadBaseAsync(
        string? pinnedCommitSha,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return CodePublicationBaseResult.Refused(CodePublicationCodes.BindingUnavailable);
        }

        if (pinnedCommitSha is not null && !GitBlobIdentity.IsValid(pinnedCommitSha))
        {
            return CodePublicationBaseResult.Refused(CodePublicationCodes.RequestInvalid);
        }

        var commitSha = pinnedCommitSha;
        if (commitSha is null)
        {
            var head = await ReadBranchAsync(BaseBranch, cancellationToken);
            if (head.CommitSha is null)
            {
                return CodePublicationBaseResult.Refused(
                    head.Code == CodePublicationCodes.BranchAbsent
                        ? CodePublicationCodes.BaseBranchMissing
                        : head.Code);
            }

            commitSha = head.CommitSha;
        }

        var (commitDocument, commitCode) = await sender.SendAsync(
            () => GitHubGitDataRequests.ReadCommit(repositoryOptions, commitSha),
            MaximumResponseBytes,
            cancellationToken);
        using (commitDocument)
        {
            if (commitDocument is null)
            {
                return CodePublicationBaseResult.Refused(commitCode!);
            }

            var commit = GitHubGitDataParser.TryReadCommit(commitDocument.RootElement);
            return commit.TreeSha is null
                ? CodePublicationBaseResult.Refused(CodePublicationCodes.ResponseMalformed)
                : await ReadTreeAsync(commitSha, commit.TreeSha, cancellationToken);
        }
    }

    public async Task<CodePublicationRefResult> ReadBranchAsync(
        string branchName,
        CancellationToken cancellationToken)
    {
        if (!repositoryOptions.IsConfigured)
        {
            return CodePublicationRefResult.Refused(CodePublicationCodes.BindingUnavailable);
        }

        if (!GitReferenceName.IsValid(branchName))
        {
            return CodePublicationRefResult.Refused(CodePublicationCodes.RequestInvalid);
        }

        var (document, code) = await sender.SendAsync(
            () => GitHubGitDataRequests.ReadRef(repositoryOptions, branchName),
            MaximumResponseBytes,
            cancellationToken,
            absentCode: CodePublicationCodes.BranchAbsent);
        using (document)
        {
            if (document is null)
            {
                return CodePublicationRefResult.Refused(code!);
            }

            return GitHubGitDataParser.TryReadObjectSha(document.RootElement) is { } sha
                ? new CodePublicationRefResult(CodePublicationCodes.BranchCreated, sha)
                : CodePublicationRefResult.Refused(CodePublicationCodes.ResponseMalformed);
        }
    }

    public async Task<CodePublicationRefResult> PushAsync(
        CodePublicationPushRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return CodePublicationRefResult.Refused(CodePublicationCodes.BindingUnavailable);
        }

        if (!GitReferenceName.IsValid(request.BranchName) ||
            !GitBlobIdentity.IsValid(request.BaseCommitSha) ||
            !GitBlobIdentity.IsValid(request.BaseTreeSha) ||
            request.Entries.Count is < 1 or > MaximumEntries ||
            request.Entries.Any(static entry =>
                string.IsNullOrEmpty(entry.RepositoryPath) ||
                (entry.FileMode != GitHubGitDataRequests.RegularFileMode &&
                 entry.FileMode != GitHubGitDataRequests.ExecutableFileMode)))
        {
            return CodePublicationRefResult.Refused(CodePublicationCodes.RequestInvalid);
        }

        var built = await BuildCommitAsync(request, cancellationToken);
        if (built.CommitSha is null)
        {
            return built;
        }

        // Everything above is content-addressed and changed nothing observable; what follows can only
        // happen once, so cancellation is settled here rather than reported as an unknown outcome.
        return cancellationToken.IsCancellationRequested
            ? CodePublicationRefResult.Refused(CodePublicationCodes.CancelledBeforeWrite)
            : await CreateRefOnceAsync(request.BranchName, built.CommitSha, cancellationToken);
    }

    private async Task<CodePublicationRefResult> BuildCommitAsync(
        CodePublicationPushRequest request,
        CancellationToken cancellationToken)
    {
        var entries = new List<(CodePublicationTreeEntry Entry, string? BlobSha)>(request.Entries.Count);
        foreach (var entry in request.Entries)
        {
            if (entry.Content is null)
            {
                entries.Add((entry, null));
                continue;
            }

            var (document, code) = await sender.SendAsync(
                () => GitHubGitDataRequests.CreateBlob(repositoryOptions, entry.Content),
                MaximumResponseBytes,
                cancellationToken);
            using (document)
            {
                if (document is null)
                {
                    return CodePublicationRefResult.Refused(code!);
                }

                if (GitHubGitDataParser.TryReadSha(document.RootElement) is not { } blobSha)
                {
                    return CodePublicationRefResult.Refused(CodePublicationCodes.ResponseMalformed);
                }

                entries.Add((entry, blobSha));
            }
        }

        var (treeDocument, treeCode) = await sender.SendAsync(
            () => GitHubGitDataRequests.CreateTree(repositoryOptions, request.BaseTreeSha, entries),
            MaximumTreeResponseBytes,
            cancellationToken);
        string treeSha;
        using (treeDocument)
        {
            if (treeDocument is null)
            {
                return CodePublicationRefResult.Refused(treeCode!);
            }

            if (GitHubGitDataParser.TryReadSha(treeDocument.RootElement) is not { } created)
            {
                return CodePublicationRefResult.Refused(CodePublicationCodes.ResponseMalformed);
            }

            treeSha = created;
        }

        return await CreateCommitAsync(request, treeSha, cancellationToken);
    }

    /// <summary>
    /// Creates the commit and refuses unless the provider built the commit that was asked for: this
    /// tree, this one parent. It runs before the reference is touched, so a provider that built
    /// something else costs a refusal rather than a branch.
    /// </summary>
    private async Task<CodePublicationRefResult> CreateCommitAsync(
        CodePublicationPushRequest request,
        string treeSha,
        CancellationToken cancellationToken)
    {
        var (document, code) = await sender.SendAsync(
            () => GitHubGitDataRequests.CreateCommit(repositoryOptions, request, treeSha),
            MaximumResponseBytes,
            cancellationToken);
        using (document)
        {
            if (document is null)
            {
                return CodePublicationRefResult.Refused(code!);
            }

            var commit = GitHubGitDataParser.TryReadCommit(document.RootElement);
            if (commit.CommitSha is null)
            {
                return CodePublicationRefResult.Refused(CodePublicationCodes.ResponseMalformed);
            }

            return string.Equals(commit.TreeSha, treeSha, StringComparison.Ordinal) &&
                string.Equals(commit.ParentSha, request.BaseCommitSha, StringComparison.Ordinal)
                ? new CodePublicationRefResult(CodePublicationCodes.BranchCreated, commit.CommitSha)
                : CodePublicationRefResult.Refused(CodePublicationCodes.CommitMismatch);
        }
    }

    /// <summary>
    /// The one mutating call. A name that is taken is never overwritten: the reference is read back,
    /// and the push is a replay when it already points at this exact commit and a refusal otherwise.
    /// </summary>
    private async Task<CodePublicationRefResult> CreateRefOnceAsync(
        string branchName,
        string commitSha,
        CancellationToken cancellationToken)
    {
        if (await sender.SendCreateAsync(
                () => GitHubGitDataRequests.CreateRef(repositoryOptions, branchName, commitSha),
                cancellationToken) is not { } status)
        {
            return CodePublicationRefResult.Refused(CodePublicationCodes.OutcomeUnknown);
        }

        if (status is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices)
        {
            return new CodePublicationRefResult(CodePublicationCodes.BranchCreated, commitSha);
        }

        if (status != HttpStatusCode.UnprocessableEntity && status != HttpStatusCode.Conflict)
        {
            return CodePublicationRefResult.Refused(
                GitHubGitDataParser.MapFailure(status) ?? CodePublicationCodes.Unavailable);
        }

        var existing = await ReadBranchAsync(branchName, cancellationToken);
        if (existing.CommitSha is null)
        {
            return CodePublicationRefResult.Refused(
                existing.Code == CodePublicationCodes.BranchAbsent
                    ? CodePublicationCodes.OutcomeUnknown
                    : existing.Code);
        }

        return string.Equals(existing.CommitSha, commitSha, StringComparison.Ordinal)
            ? new CodePublicationRefResult(CodePublicationCodes.BranchAlreadyAtCommit, commitSha)
            : CodePublicationRefResult.Refused(CodePublicationCodes.BranchDiverged);
    }

    /// <summary>
    /// Opens one pull request, after proving the head is still the approved commit and after asking
    /// whether a pull request for that head already exists.
    /// </summary>
    /// <remarks>
    /// The order is the whole at-most-once argument. The reference read refuses a head someone moved
    /// since the approval; the listing read is the marker search, and it is unconditional, so no create
    /// is ever sent without that question having been answered first, and a create whose answer never
    /// arrives is settled later by that same read.
    /// </remarks>
    public async Task<CodePublicationPullRequestResult> CreatePullRequestAsync(
        CodePublicationPullRequestRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return CodePublicationPullRequestResult.Refused(CodePublicationCodes.BindingUnavailable);
        }

        if (!GitReferenceName.IsValid(request.HeadBranch) ||
            !GitBlobIdentity.IsValid(request.HeadCommitSha) ||
            !GitHubPullRequestOperations.IsPublishable(request))
        {
            return CodePublicationPullRequestResult.Refused(CodePublicationCodes.RequestInvalid);
        }

        var head = await ReadBranchAsync(request.HeadBranch, cancellationToken);
        if (head.CommitSha is null)
        {
            return CodePublicationPullRequestResult.Refused(
                head.Code == CodePublicationCodes.BranchAbsent
                    ? CodePublicationCodes.HeadBranchMissing
                    : head.Code);
        }

        if (!string.Equals(head.CommitSha, request.HeadCommitSha, StringComparison.Ordinal))
        {
            return CodePublicationPullRequestResult.Refused(CodePublicationCodes.HeadBranchDiverged);
        }

        var existing = await pullRequests.FindAsync(
            request.HeadBranch, request.HeadCommitSha, BaseBranch, cancellationToken);
        if (existing.Code != CodePublicationCodes.PullRequestAbsent)
        {
            return existing;
        }

        return cancellationToken.IsCancellationRequested
            ? CodePublicationPullRequestResult.Refused(CodePublicationCodes.CancelledBeforeWrite)
            : await pullRequests.OpenAsync(request, BaseBranch, cancellationToken);
    }

    private async Task<CodePublicationBaseResult> ReadTreeAsync(
        string commitSha,
        string treeSha,
        CancellationToken cancellationToken)
    {
        var (document, code) = await sender.SendAsync(
            () => GitHubGitDataRequests.ReadTree(repositoryOptions, treeSha),
            MaximumTreeResponseBytes,
            cancellationToken);
        using (document)
        {
            if (document is null)
            {
                return CodePublicationBaseResult.Refused(code!);
            }

            var (blobs, modes, failure) = GitHubGitDataParser.TryReadTree(document.RootElement);
            return blobs is null || modes is null
                ? CodePublicationBaseResult.Refused(failure ?? CodePublicationCodes.ResponseMalformed)
                : new CodePublicationBaseResult(
                    CodePublicationCodes.BaseRead, commitSha, treeSha, blobs, modes);
        }
    }
}
