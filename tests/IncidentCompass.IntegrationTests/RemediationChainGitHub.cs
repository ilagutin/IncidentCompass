using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// One in-process stand-in for every GitHub call the publication chain can make: the Git Data half a
/// push uses, the pull-request half, and the issue half both governed comments use.
/// </summary>
/// <remarks>
/// <para>
/// It is deliberately a single handler shared by every adapter in the chain rather than one stub per
/// hop. A per-hop stub can only say what that hop asked for; one repository state can say what the
/// whole walk did to it, which is the question the chain-level test exists to answer: how many
/// mutating calls reached the provider in total, which ones they were, and whether a second dispatch
/// added any.
/// </para>
/// <para>
/// <b>Any path it does not recognize throws.</b> That is what makes "zero unauthorized calls" an
/// assertion rather than an omission: a request this chain is not supposed to make fails the
/// dispatch that made it instead of being quietly answered.
/// </para>
/// <para>
/// <b>Blob names are real git object names.</b> A push is only meaningful if the bytes it sends are
/// the bytes the approval was taken over, and the correspondence proof compares local blob ids with
/// the ones this stand-in reports. Computing them the way git does keeps both sides honest; nothing
/// is authorized by one.
/// </para>
/// </remarks>
internal sealed class RemediationChainGitHub(string owner, string repository) : HttpMessageHandler
{
    public const string BaseCommitSha = "1111111111111111111111111111111111111111";
    public const string BaseTreeSha = "2222222222222222222222222222222222222222";
    public const string PushedTreeSha = "3333333333333333333333333333333333333333";
    public const string PushedCommitSha = "4444444444444444444444444444444444444444";

    private readonly Dictionary<string, (string Sha, string Mode)> baseTree = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> branches = new(StringComparer.Ordinal);
    private readonly Dictionary<int, List<string>> issueComments = [];
    private readonly HashSet<int> issues = [];
    private readonly List<PullRequestRecord> pullRequests = [];
    private int nextCommentId = 900;
    private int nextPullRequestNumber = 17;

    /// <summary>Every call, as "METHOD path", in order.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>Only the calls that change provider state, as "METHOD path", in order.</summary>
    public List<string> Mutations { get; } = [];

    /// <summary>The decoded content of every blob a push created.</summary>
    public List<string> PushedBlobContents { get; } = [];

    /// <summary>The paths a push named in its tree create.</summary>
    public List<string> PushedTreePaths { get; } = [];

    /// <summary>The commit message of every commit a push created.</summary>
    public List<string> PushedCommitMessages { get; } = [];

    /// <summary>The reference names a push tried to create, including attempts that were refused.</summary>
    public List<string> CreatedReferences { get; } = [];

    /// <summary>How many of those attempts actually brought a branch into existence.</summary>
    public int EffectiveReferenceCreates { get; private set; }

    /// <summary>The title and body of every pull request that was opened.</summary>
    public List<(string Title, string Body, string Head, string Base)> OpenedPullRequests { get; } = [];

    /// <summary>Every comment body that was posted, with the issue it landed on.</summary>
    public List<(int IssueNumber, string Body)> PostedComments { get; } = [];

    public void AddBaseFile(string repositoryPath, byte[] content) =>
        baseTree[repositoryPath] = (BlobId(content), "100644");

    /// <summary>The commit one branch points at, or null when the branch does not exist.</summary>
    public string? BranchCommit(string branch) =>
        branches.TryGetValue(branch, out var commit) ? commit : null;

    public void AddIssue(int number)
    {
        issues.Add(number);
        issueComments.TryAdd(number, []);
    }

    public static string BlobId(ReadOnlySpan<byte> content)
    {
        var header = Encoding.ASCII.GetBytes(
            "blob " + content.Length.ToString(CultureInfo.InvariantCulture) + "\0");
#pragma warning disable CA5350 // A git object name is SHA-1 by format; nothing is authorized by one.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
#pragma warning restore CA5350
        hash.AppendData(header);
        hash.AppendData(content);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        Calls.Add(request.Method + " " + path);
        if (request.Method != HttpMethod.Get)
        {
            Mutations.Add(request.Method + " " + path);
        }

        var prefix = "/repos/" + owner + "/" + repository;
        if (!path.StartsWith(prefix + "/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unexpected provider repository: " + path);
        }

        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return Route(request, path[prefix.Length..], request.RequestUri.Query, body);
    }

    private HttpResponseMessage Route(
        HttpRequestMessage request,
        string route,
        string query,
        string? body)
    {
        if (request.Method == HttpMethod.Get)
        {
            return route switch
            {
                _ when route.StartsWith("/git/ref/heads/", StringComparison.Ordinal) =>
                    ReadReference(route["/git/ref/heads/".Length..]),
                _ when route.StartsWith("/git/commits/", StringComparison.Ordinal) =>
                    ReadCommit(route["/git/commits/".Length..]),
                _ when route.StartsWith("/git/trees/", StringComparison.Ordinal) => ReadTree(),
                "/pulls" => ListPullRequests(query),
                _ when route.StartsWith("/issues/", StringComparison.Ordinal) => ReadIssue(route),
                _ => throw new InvalidOperationException("Unexpected provider read: " + route)
            };
        }

        if (request.Method != HttpMethod.Post)
        {
            throw new InvalidOperationException(
                "Unexpected provider write method: " + request.Method + " " + route);
        }

        return route switch
        {
            "/git/blobs" => CreateBlob(body!),
            "/git/trees" => CreateTree(body!),
            "/git/commits" => CreateCommit(body!),
            "/git/refs" => CreateReference(body!),
            "/pulls" => CreatePullRequest(body!),
            _ when route.StartsWith("/issues/", StringComparison.Ordinal) &&
                route.EndsWith("/comments", StringComparison.Ordinal) =>
                CreateComment(route, body!),
            _ => throw new InvalidOperationException("Unexpected provider write: " + route)
        };
    }

    private HttpResponseMessage ReadReference(string branch)
    {
        if (string.Equals(branch, "main", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, new { @object = new { sha = BaseCommitSha } });
        }

        return branches.TryGetValue(branch, out var commit)
            ? Json(HttpStatusCode.OK, new { @object = new { sha = commit } })
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage ReadCommit(string commitSha) =>
        string.Equals(commitSha, BaseCommitSha, StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, new
            {
                sha = BaseCommitSha,
                tree = new { sha = BaseTreeSha },
                parents = Array.Empty<object>()
            })
            : new HttpResponseMessage(HttpStatusCode.NotFound);

    private HttpResponseMessage ReadTree() => Json(HttpStatusCode.OK, new
    {
        truncated = false,
        tree = baseTree
            .Select(entry => new
            {
                path = entry.Key,
                type = "blob",
                mode = entry.Value.Mode,
                sha = entry.Value.Sha
            })
            .ToArray()
    });

    private HttpResponseMessage CreateBlob(string body)
    {
        using var document = JsonDocument.Parse(body);
        var content = Convert.FromBase64String(document.RootElement.GetProperty("content").GetString()!);
        PushedBlobContents.Add(Encoding.UTF8.GetString(content));
        return Json(HttpStatusCode.Created, new { sha = BlobId(content) });
    }

    private HttpResponseMessage CreateTree(string body)
    {
        using var document = JsonDocument.Parse(body);
        foreach (var entry in document.RootElement.GetProperty("tree").EnumerateArray())
        {
            PushedTreePaths.Add(entry.GetProperty("path").GetString()!);
        }

        return Json(HttpStatusCode.Created, new { sha = PushedTreeSha });
    }

    private HttpResponseMessage CreateCommit(string body)
    {
        using var document = JsonDocument.Parse(body);
        PushedCommitMessages.Add(document.RootElement.GetProperty("message").GetString()!);
        return Json(HttpStatusCode.Created, new
        {
            sha = PushedCommitSha,
            tree = new { sha = PushedTreeSha },
            parents = new[] { new { sha = BaseCommitSha } }
        });
    }

    private HttpResponseMessage CreateReference(string body)
    {
        using var document = JsonDocument.Parse(body);
        var name = document.RootElement.GetProperty("ref").GetString()!;
        CreatedReferences.Add(name);
        var branch = name["refs/heads/".Length..];
        if (!branches.TryAdd(branch, document.RootElement.GetProperty("sha").GetString()!))
        {
            return Json(HttpStatusCode.UnprocessableEntity, new { message = "Reference already exists" });
        }

        EffectiveReferenceCreates++;
        return Json(HttpStatusCode.Created, new { @ref = name });
    }

    private HttpResponseMessage ListPullRequests(string query)
    {
        var head = Value(query, "head=");
        var headBranch = head[(head.IndexOf(':', StringComparison.Ordinal) + 1)..];
        return Json(
            HttpStatusCode.OK,
            pullRequests
                .Where(pull => string.Equals(pull.Head, headBranch, StringComparison.Ordinal))
                .Select(Render)
                .ToArray());
    }

    private HttpResponseMessage CreatePullRequest(string body)
    {
        using var document = JsonDocument.Parse(body);
        var head = document.RootElement.GetProperty("head").GetString()!;
        var target = document.RootElement.GetProperty("base").GetString()!;
        OpenedPullRequests.Add((
            document.RootElement.GetProperty("title").GetString()!,
            document.RootElement.GetProperty("body").GetString()!,
            head,
            target));
        var record = new PullRequestRecord(
            nextPullRequestNumber++, head, branches[head], target);
        pullRequests.Add(record);
        return Json(HttpStatusCode.Created, Render(record));
    }

    private HttpResponseMessage ReadIssue(string route)
    {
        var rest = route["/issues/".Length..];
        var number = int.Parse(
            rest.EndsWith("/comments", StringComparison.Ordinal)
                ? rest[..^"/comments".Length]
                : rest,
            CultureInfo.InvariantCulture);
        if (!issues.Contains(number))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        return rest.EndsWith("/comments", StringComparison.Ordinal)
            ? Json(
                HttpStatusCode.OK,
                issueComments[number].Select((text, index) => Comment(number, index, text)).ToArray())
            : Json(HttpStatusCode.OK, new
            {
                number,
                html_url = $"https://github.com/{owner}/{repository}/issues/{number}"
            });
    }

    private HttpResponseMessage CreateComment(string route, string body)
    {
        var number = int.Parse(
            route["/issues/".Length..^"/comments".Length],
            CultureInfo.InvariantCulture);
        if (!issues.Contains(number))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        using var document = JsonDocument.Parse(body);
        var text = document.RootElement.GetProperty("body").GetString()!;
        PostedComments.Add((number, text));
        issueComments[number].Add(text);
        return Json(HttpStatusCode.Created, Comment(number, issueComments[number].Count - 1, text));
    }

    private object Comment(int issueNumber, int index, string text) => new
    {
        id = nextCommentId + index,
        html_url = $"https://github.com/{owner}/{repository}/issues/{issueNumber}#issuecomment-{nextCommentId + index}",
        issue_url = $"https://api.github.com/repos/{owner}/{repository}/issues/{issueNumber}",
        body = text
    };

    private static object Render(PullRequestRecord record) => new
    {
        number = record.Number,
        head = new { @ref = record.Head, sha = record.HeadCommitSha },
        @base = new { @ref = record.Base }
    };

    private static string Value(string query, string key)
    {
        var start = query.IndexOf(key, StringComparison.Ordinal) + key.Length;
        var end = query.IndexOf('&', start);
        return end < 0 ? query[start..] : query[start..end];
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, object body) =>
        new(statusCode)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

    private sealed record PullRequestRecord(
        int Number,
        string Head,
        string HeadCommitSha,
        string Base);
}
