using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Infrastructure.Tickets;

namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// Builds the request shapes a governed push and pull request use, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no request here that could move, delete or merge a reference.</b> The only writes are
/// <see cref="CreateRef" />, which is a create and fails when the name is taken, and
/// <see cref="CreatePullRequest" />, which opens one. There is no <c>PATCH</c>, no <c>PUT</c>, no
/// <c>DELETE</c>, no <c>/merge</c> endpoint and no GraphQL call anywhere in this file, so neither a
/// merge nor an automatic merge nor a repository-settings change can be expressed; and none could be
/// reached from the gateway anyway, because the port above it has no method that would call one. That
/// is the structural form of "never force-push, never delete a branch, never merge": not a rule the
/// code follows, but a request it cannot build.
/// </para>
/// <para>
/// <b>Blob content travels base64-encoded.</b> The whole feature is a claim about bytes, and sending
/// file content as a JSON string would make that claim depend on how two systems agree to encode it.
/// Base64 removes the question: the blob the provider stores is the bytes the patched copy held.
/// </para>
/// </remarks>
internal static class GitHubGitDataRequests
{
    /// <summary>The identity every commit this product creates is authored and committed by.</summary>
    public const string AuthorName = "IncidentCompass";

    /// <summary>
    /// A reserved-for-invalid-use address. A push must not be attributable to a person who did not
    /// write the change, and a plausible-looking mailbox in a commit header is exactly that.
    /// </summary>
    public const string AuthorEmail = "incidentcompass@invalid";

    public const string RegularFileMode = "100644";
    public const string ExecutableFileMode = "100755";
    public const string BlobType = "blob";

    public static HttpRequestMessage ReadRef(GitHubIssuesOptions options, string branchName) =>
        Get(options, $"{Repository(options)}/git/ref/heads/{branchName}");

    public static HttpRequestMessage ReadCommit(GitHubIssuesOptions options, string commitSha) =>
        Get(options, $"{Repository(options)}/git/commits/{commitSha}");

    public static HttpRequestMessage ReadTree(GitHubIssuesOptions options, string treeSha) =>
        Get(options, $"{Repository(options)}/git/trees/{treeSha}?recursive=1");

    public static HttpRequestMessage CreateBlob(GitHubIssuesOptions options, byte[] content) =>
        Post(options, $"{Repository(options)}/git/blobs", new JsonObject
        {
            ["content"] = Convert.ToBase64String(content),
            ["encoding"] = "base64"
        });

    public static HttpRequestMessage CreateTree(
        GitHubIssuesOptions options,
        string baseTreeSha,
        IReadOnlyList<(CodePublicationTreeEntry Entry, string? BlobSha)> entries)
    {
        var tree = new JsonArray();
        foreach (var (entry, blobSha) in entries)
        {
            tree.Add(new JsonObject
            {
                ["mode"] = entry.FileMode,
                ["path"] = entry.RepositoryPath,
                ["sha"] = blobSha,
                ["type"] = BlobType
            });
        }

        return Post(options, $"{Repository(options)}/git/trees", new JsonObject
        {
            ["base_tree"] = baseTreeSha,
            ["tree"] = tree
        });
    }

    public static HttpRequestMessage CreateCommit(
        GitHubIssuesOptions options,
        CodePublicationPushRequest request,
        string treeSha)
    {
        var identity = new JsonObject
        {
            ["date"] = BranchPushPayloadFactory.FormatTimestamp(request.CommitTimestampUtc),
            ["email"] = AuthorEmail,
            ["name"] = AuthorName
        };
        return Post(options, $"{Repository(options)}/git/commits", new JsonObject
        {
            ["author"] = identity,
            ["committer"] = identity.DeepClone(),
            ["message"] = request.CommitMessage,
            ["parents"] = new JsonArray(request.BaseCommitSha),
            ["tree"] = treeSha
        });
    }

    public static HttpRequestMessage CreateRef(
        GitHubIssuesOptions options,
        string branchName,
        string commitSha) =>
        Post(options, $"{Repository(options)}/git/refs", new JsonObject
        {
            ["ref"] = "refs/heads/" + branchName,
            ["sha"] = commitSha
        });

    /// <summary>
    /// Asks for whatever pull request answers for one head branch against one base, open or closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Closed ones count.</b> Filtering to open pull requests would make a closed one invisible, and
    /// an adapter that cannot see the pull request it already opened would open a second. The page size
    /// is two rather than one so that ambiguity is detectable instead of being hidden by the bound.
    /// </para>
    /// <para>
    /// Both names are allowlisted before they reach here, so nothing in this query needs escaping.
    /// </para>
    /// </remarks>
    public static HttpRequestMessage ReadPullRequests(
        GitHubIssuesOptions options,
        string headBranch,
        string baseBranch) =>
        Get(
            options,
            $"{Repository(options)}/pulls?head={options.Owner}:{headBranch}&base={baseBranch}" +
            "&state=all&sort=created&direction=asc&per_page=2");

    /// <summary>
    /// Opens one pull request. The body has exactly four properties and there is no fifth it could
    /// carry: no draft flag, no merge method, no auto-merge, no reviewer and no label.
    /// </summary>
    public static HttpRequestMessage CreatePullRequest(
        GitHubIssuesOptions options,
        CodePublicationPullRequestRequest request,
        string baseBranch) =>
        Post(options, $"{Repository(options)}/pulls", new JsonObject
        {
            ["base"] = baseBranch,
            ["body"] = request.Body,
            ["head"] = request.HeadBranch,
            ["title"] = request.Title
        });

    private static string Repository(GitHubIssuesOptions options) =>
        $"/repos/{options.Owner}/{options.Repository}";

    private static HttpRequestMessage Get(GitHubIssuesOptions options, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        GitHubIssueCreateRequestFactory.AddGitHubHeaders(request, options.Token!);
        return request;
    }

    private static HttpRequestMessage Post(GitHubIssuesOptions options, string path, JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(
                Encoding.UTF8.GetBytes(CanonicalJsonSerializer.Canonicalize(body)))
        };
        GitHubIssueCreateRequestFactory.AddGitHubHeaders(request, options.Token!);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };
        return request;
    }
}
