using System.Net;
using System.Text;
using System.Text.Json;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Infrastructure.Remediation;
using IncidentCompass.Infrastructure.Tickets;
using Microsoft.Extensions.Options;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// The provider half of a governed push, against a stand-in that answers like the Git Data API.
/// </summary>
/// <remarks>
/// No request leaves the process: the adapter is constructed with a handler that answers every call,
/// which is how the issue adapters are tested for the same reason. What is asserted is the shape of
/// the traffic, not the provider's behaviour: how many mutating calls there are, which ones they are,
/// and that nothing this adapter can be asked to do produces a request that would move, delete or
/// merge a reference.
/// </remarks>
public sealed class GitHubCodePublicationGatewayTests
{
    private const string BaseCommit = "1111111111111111111111111111111111111111";
    private const string BaseTree = "2222222222222222222222222222222222222222";
    private const string NewTree = "3333333333333333333333333333333333333333";
    private const string NewCommit = "4444444444444444444444444444444444444444";
    private const string BlobSha = "5555555555555555555555555555555555555555";
    private const string BranchName = "incidentcompass/remediation/abcdef01234567890abcdef012345678";

    [Fact]
    public async Task ReadingTheBaseWalksRefThenCommitThenTreeAndReportsOnlyRegularFiles()
    {
        var handler = new RecordingHandler(Standard());
        using var gateway = CreateGateway(handler);

        var result = await gateway.ReadBaseAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(CodePublicationCodes.BaseRead, result.Code);
        Assert.Equal(BaseCommit, result.CommitSha);
        Assert.Equal(BaseTree, result.TreeSha);
        Assert.Equal(["src/A.cs", "tools/run.sh"], result.BlobsByPath.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("100755", result.FileModesByPath["tools/run.sh"]);
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    [Fact]
    public async Task ATruncatedListingRefusesRatherThanNarrowingTheComparison()
    {
        var handler = new RecordingHandler(request => IsTree(request)
            ? Json(HttpStatusCode.OK, new { truncated = true, tree = Array.Empty<object>() })
            : Standard()(request));
        using var gateway = CreateGateway(handler);

        var result = await gateway.ReadBaseAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(CodePublicationCodes.BaseTreeTruncated, result.Code);
    }

    [Theory]
    [InlineData("blob", "120000")]
    [InlineData("commit", "160000")]
    public async Task AnEntryThisProductCannotReproduceRefusesTheWholeRead(string type, string mode)
    {
        var handler = new RecordingHandler(request => IsTree(request)
            ? Json(HttpStatusCode.OK, new
            {
                truncated = false,
                tree = new[] { new { path = "link", type, mode, sha = BlobSha } }
            })
            : Standard()(request));
        using var gateway = CreateGateway(handler);

        var result = await gateway.ReadBaseAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(CodePublicationCodes.BaseTreeUnsupportedEntry, result.Code);
    }

    /// <summary>
    /// The shape the whole at-most-once argument rests on: the objects are content-addressed and the
    /// reference create is the only call that mutates anything.
    /// </summary>
    [Fact]
    public async Task APushMakesExactlyOneReferenceCreateAndNoOtherReferenceWrite()
    {
        var handler = new RecordingHandler(Standard());
        using var gateway = CreateGateway(handler);

        var result = await gateway.PushAsync(PushRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(CodePublicationCodes.BranchCreated, result.Code);
        Assert.Equal(NewCommit, result.CommitSha);
        Assert.Equal(1, handler.Paths.Count(path => path.EndsWith("/git/refs", StringComparison.Ordinal)));
        Assert.DoesNotContain(handler.Methods, method =>
            method == HttpMethod.Patch || method == HttpMethod.Delete || method == HttpMethod.Put);
        Assert.DoesNotContain(handler.Paths, path => path.Contains("/merges", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(handler.BodyFor("/git/refs")!);
        Assert.Equal("refs/heads/" + BranchName, body.RootElement.GetProperty("ref").GetString());
        Assert.Equal(NewCommit, body.RootElement.GetProperty("sha").GetString());
    }

    /// <summary>
    /// Replay: the objects come back with the same names, the reference create is refused because the
    /// name is taken, and the adapter reads rather than overwrites. Still exactly one create attempt.
    /// </summary>
    [Fact]
    public async Task AReplayFindsTheBranchAlreadyAtTheCommitAndWritesNothing()
    {
        var handler = new RecordingHandler(request => IsRefCreate(request)
            ? Json(HttpStatusCode.UnprocessableEntity, new { message = "Reference already exists" })
            : Standard(existingBranchCommit: NewCommit)(request));
        using var gateway = CreateGateway(handler);

        var result = await gateway.PushAsync(PushRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(CodePublicationCodes.BranchAlreadyAtCommit, result.Code);
        Assert.Equal(NewCommit, result.CommitSha);
        Assert.Equal(1, handler.Paths.Count(path => path.EndsWith("/git/refs", StringComparison.Ordinal)));
        Assert.Equal(1, handler.Paths.Count(path =>
            path.EndsWith("/git/ref/heads/" + BranchName, StringComparison.Ordinal)));
        Assert.DoesNotContain(handler.Methods, method =>
            method == HttpMethod.Patch || method == HttpMethod.Delete || method == HttpMethod.Put);
    }

    [Fact]
    public async Task ABranchPointingSomewhereElseIsNeverOverwritten()
    {
        var handler = new RecordingHandler(request => IsRefCreate(request)
            ? Json(HttpStatusCode.UnprocessableEntity, new { message = "Reference already exists" })
            : Standard(existingBranchCommit: "9999999999999999999999999999999999999999")(request));
        using var gateway = CreateGateway(handler);

        var result = await gateway.PushAsync(PushRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(CodePublicationCodes.BranchDiverged, result.Code);
        Assert.Null(result.CommitSha);
        Assert.DoesNotContain(handler.Methods, method => method == HttpMethod.Patch);
    }

    [Fact]
    public async Task ACommitTheProviderBuiltDifferentlyRefusesBeforeTheReferenceIsTouched()
    {
        var handler = new RecordingHandler(request => IsCommitCreate(request)
            ? Json(HttpStatusCode.Created, new
            {
                sha = NewCommit,
                tree = new { sha = NewTree },
                parents = new[] { new { sha = "8888888888888888888888888888888888888888" } }
            })
            : Standard()(request));
        using var gateway = CreateGateway(handler);

        var result = await gateway.PushAsync(PushRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(CodePublicationCodes.CommitMismatch, result.Code);
        Assert.DoesNotContain(handler.Paths, path => path.EndsWith("/git/refs", StringComparison.Ordinal));
    }

    /// <summary>
    /// A reference create whose answer never arrived is an unknown outcome, which is durable and is
    /// settled later by one read rather than by trying again.
    /// </summary>
    [Fact]
    public async Task AReferenceCreateWithNoAnswerIsAnUnknownOutcome()
    {
        var handler = new RecordingHandler(request => IsRefCreate(request)
            ? throw new HttpRequestException("connection reset")
            : Standard()(request));
        using var gateway = CreateGateway(handler);

        var result = await gateway.PushAsync(PushRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(CodePublicationCodes.OutcomeUnknown, result.Code);
        Assert.Null(result.CommitSha);
    }

    /// <summary>
    /// A read creates nothing, so it must not answer with the code that says a branch was created.
    /// The code is logged and persisted on an action row, and callers only ever test the commit for
    /// null, so a borrowed code would never be caught by the paths that consume this result.
    /// </summary>
    [Fact]
    public async Task ReadingAnExistingBranchAnswersThatItWasReadAndNeverThatItWasCreated()
    {
        var handler = new RecordingHandler(Standard(NewCommit));
        using var gateway = CreateGateway(handler);

        var result = await gateway.ReadBranchAsync(BranchName, TestContext.Current.CancellationToken);

        Assert.Equal(CodePublicationCodes.BranchRead, result.Code);
        Assert.NotEqual(CodePublicationCodes.BranchCreated, result.Code);
        Assert.Equal(NewCommit, result.CommitSha);
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    [Fact]
    public async Task ReadingAnAbsentBranchIsItsOwnAnswerAndNotAFailure()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var gateway = CreateGateway(handler);

        var result = await gateway.ReadBranchAsync(BranchName, TestContext.Current.CancellationToken);

        Assert.Equal(CodePublicationCodes.BranchAbsent, result.Code);
        Assert.Single(handler.Methods);
    }

    /// <summary>
    /// Nothing outside a small alphabet ever becomes part of a URL, so a name carrying a traversal
    /// segment is refused rather than escaped.
    /// </summary>
    [Theory]
    [InlineData("../../main")]
    [InlineData("feature/..%2fmain")]
    [InlineData("name with spaces")]
    public async Task ABranchNameOutsideTheAllowlistNeverReachesARequest(string branchName)
    {
        var handler = new RecordingHandler(Standard());
        using var gateway = CreateGateway(handler);

        var result = await gateway.ReadBranchAsync(branchName, TestContext.Current.CancellationToken);

        Assert.Equal(CodePublicationCodes.RequestInvalid, result.Code);
        Assert.Empty(handler.Methods);
    }

    [Fact]
    public async Task AnUnconfiguredHostRefusesWithoutReachingTheProvider()
    {
        var handler = new RecordingHandler(Standard());
        using var gateway = CreateGateway(handler, baseBranch: null);

        var result = await gateway.ReadBaseAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(CodePublicationCodes.BindingUnavailable, result.Code);
        Assert.Empty(handler.Methods);
    }

    [Fact]
    public void RepointingTheBaseBranchChangesTheBindingFingerprint()
    {
        using var main = CreateGateway(new RecordingHandler(Standard()));
        using var release = CreateGateway(new RecordingHandler(Standard()), baseBranch: "release");
        using var otherRepository = CreateGateway(
            new RecordingHandler(Standard()), repository: "other");

        Assert.NotEqual(main.BindingFingerprint, release.BindingFingerprint);
        Assert.NotEqual(main.BindingFingerprint, otherRepository.BindingFingerprint);
    }

    private static CodePublicationPushRequest PushRequest() => new(
        BranchName,
        BaseCommit,
        BaseTree,
        [new CodePublicationTreeEntry("src/A.cs", "100644", "namespace B;\n"u8.ToArray())],
        "Governed remediation\n",
        new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));

    private static GitHubCodePublicationGateway CreateGateway(
        HttpMessageHandler handler,
        string? baseBranch = "main",
        string repository = "checkout") =>
        new(
            Options.Create(new GitHubIssuesOptions
            {
                Owner = "acme",
                Repository = repository,
                Token = "code-publication-token-sentinel",
                TimeoutSeconds = 10
            }),
            Options.Create(new GitHubCodePublicationOptions
            {
                BaseBranch = baseBranch,
                TimeoutSeconds = 10
            }),
            handler);

    private static Func<HttpRequestMessage, HttpResponseMessage> Standard(
        string? existingBranchCommit = null) =>
        request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.Contains("/git/ref/heads/", StringComparison.Ordinal))
            {
                var isBase = path.EndsWith("/main", StringComparison.Ordinal);
                if (!isBase && existingBranchCommit is null)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                return Json(HttpStatusCode.OK, new
                {
                    @object = new { sha = isBase ? BaseCommit : existingBranchCommit }
                });
            }

            if (request.Method == HttpMethod.Get && path.Contains("/git/commits/", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, new
                {
                    sha = BaseCommit,
                    tree = new { sha = BaseTree },
                    parents = Array.Empty<object>()
                });
            }

            if (IsTree(request))
            {
                return Json(HttpStatusCode.OK, new
                {
                    truncated = false,
                    tree = new object[]
                    {
                        new { path = "src", type = "tree", mode = "040000", sha = BaseTree },
                        new { path = "src/A.cs", type = "blob", mode = "100644", sha = BlobSha },
                        new { path = "tools/run.sh", type = "blob", mode = "100755", sha = BlobSha }
                    }
                });
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/git/blobs", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.Created, new { sha = BlobSha });
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/git/trees", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.Created, new { sha = NewTree });
            }

            if (IsCommitCreate(request))
            {
                return Json(HttpStatusCode.Created, new
                {
                    sha = NewCommit,
                    tree = new { sha = NewTree },
                    parents = new[] { new { sha = BaseCommit } }
                });
            }

            return IsRefCreate(request)
                ? Json(HttpStatusCode.Created, new { @ref = "refs/heads/" + BranchName })
                : throw new InvalidOperationException("Unexpected provider call: " + path);
        };

    private static bool IsTree(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get &&
        request.RequestUri!.AbsolutePath.Contains("/git/trees/", StringComparison.Ordinal);

    private static bool IsCommitCreate(HttpRequestMessage request) =>
        request.Method == HttpMethod.Post &&
        request.RequestUri!.AbsolutePath.EndsWith("/git/commits", StringComparison.Ordinal);

    private static bool IsRefCreate(HttpRequestMessage request) =>
        request.Method == HttpMethod.Post &&
        request.RequestUri!.AbsolutePath.EndsWith("/git/refs", StringComparison.Ordinal);

    private static HttpResponseMessage Json(HttpStatusCode statusCode, object body) =>
        new(statusCode)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];

        public List<string> Paths { get; } = [];

        public List<string?> Bodies { get; } = [];

        public string? BodyFor(string pathSuffix)
        {
            for (var index = 0; index < Paths.Count; index++)
            {
                if (Paths[index].EndsWith(pathSuffix, StringComparison.Ordinal))
                {
                    return Bodies[index];
                }
            }

            return null;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            Paths.Add(request.RequestUri!.AbsolutePath);
            Bodies.Add(request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return response(request);
        }
    }
}
