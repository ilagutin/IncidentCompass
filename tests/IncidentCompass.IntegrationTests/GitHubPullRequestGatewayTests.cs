using System.Net;
using System.Text;
using System.Text.Json;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Infrastructure.Remediation;
using IncidentCompass.Infrastructure.Tickets;
using Microsoft.Extensions.Options;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// The provider half of a governed pull request, against a stand-in that answers like the GitHub API.
/// </summary>
/// <remarks>
/// No request leaves the process: the adapter is constructed with a handler that answers every call,
/// exactly as the push and the issue adapters are tested. What is asserted is the shape of the traffic
/// rather than the provider's behaviour: how many mutating calls there are, that the read which decides
/// whether to make one always precedes it, and that nothing this adapter can be asked to do produces a
/// request that would merge, enable a merge, move a reference or change a repository setting.
/// </remarks>
public sealed class GitHubPullRequestGatewayTests
{
    private const string HeadBranch = "incidentcompass/remediation/abcdef01234567890abcdef012345678";
    private const string HeadCommit = "4444444444444444444444444444444444444444";
    private const string OtherCommit = "9999999999999999999999999999999999999999";
    private const string Token = "pull-request-token-sentinel";

    [Fact]
    public async Task OpeningReadsTheHeadThenTheListingThenMakesExactlyOneCreate()
    {
        var handler = new PullRequestHandler(Standard());
        using var gateway = CreateGateway(handler);

        var result = await gateway.CreatePullRequestAsync(
            Request(), TestContext.Current.CancellationToken);

        Assert.Equal(CodePublicationCodes.PullRequestOpened, result.Code);
        Assert.Equal(17, result.Number);
        Assert.Equal([HttpMethod.Get, HttpMethod.Get, HttpMethod.Post], handler.Methods);
        Assert.EndsWith("/git/ref/heads/" + HeadBranch, handler.Paths[0], StringComparison.Ordinal);
        Assert.Contains("/pulls?head=acme:" + HeadBranch, handler.Paths[1], StringComparison.Ordinal);
        Assert.Contains("base=main", handler.Paths[1], StringComparison.Ordinal);
        Assert.Equal("/repos/acme/checkout/pulls", handler.Paths[2]);
    }

    /// <summary>
    /// The shape the whole "never merge" argument rests on. Nothing this adapter can be driven to do
    /// produces a method or a path that could merge, enable a merge, move a reference or edit a
    /// repository, because no request factory builds one.
    /// </summary>
    [Fact]
    public async Task NothingTheAdapterSendsCouldMergeOrChangeARepository()
    {
        var handler = new PullRequestHandler(Standard());
        using var gateway = CreateGateway(handler);

        await gateway.CreatePullRequestAsync(Request(), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(handler.Methods, static method =>
            method == HttpMethod.Put || method == HttpMethod.Patch || method == HttpMethod.Delete);
        Assert.All(handler.Paths, path =>
        {
            Assert.DoesNotContain("/merge", path, StringComparison.Ordinal);
            Assert.DoesNotContain("/graphql", path, StringComparison.Ordinal);
        });
        using var body = JsonDocument.Parse(handler.BodyFor("/pulls")!);
        Assert.Equal(
            ["base", "body", "head", "title"],
            body.RootElement.EnumerateObject().Select(static property => property.Name));
    }

    /// <summary>
    /// Replay: the listing answers with the pull request the create would have made, so nothing is
    /// created and the same number comes back.
    /// </summary>
    [Fact]
    public async Task AReplayFindsTheExistingPullRequestAndCreatesNothing()
    {
        var handler = new PullRequestHandler(Standard(existing: Existing(17, HeadCommit)));
        using var gateway = CreateGateway(handler);

        var result = await gateway.CreatePullRequestAsync(
            Request(), TestContext.Current.CancellationToken);

        Assert.Equal(CodePublicationCodes.PullRequestAlreadyOpen, result.Code);
        Assert.Equal(17, result.Number);
        Assert.Equal([HttpMethod.Get, HttpMethod.Get], handler.Methods);
        Assert.DoesNotContain(handler.Methods, static method => method == HttpMethod.Post);
    }

    /// <summary>
    /// The bound to the head an earlier approved push confirmed. A branch that moved since, or that was
    /// never created, refuses before the listing is even read.
    /// </summary>
    [Fact]
    public async Task AHeadThatMovedSinceTheApprovalRefusesBeforeAnythingElseIsRead()
    {
        var handler = new PullRequestHandler(Standard(headCommit: OtherCommit));
        using var gateway = CreateGateway(handler);

        var result = await gateway.CreatePullRequestAsync(
            Request(), TestContext.Current.CancellationToken);

        Assert.Equal(CodePublicationCodes.HeadBranchDiverged, result.Code);
        Assert.Null(result.Number);
        Assert.Single(handler.Methods);
    }

    [Fact]
    public async Task AnAbsentHeadRefusesAndCreatesNothing()
    {
        var handler = new PullRequestHandler(request => IsRefRead(request)
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : throw new InvalidOperationException("Unexpected provider call."));
        using var gateway = CreateGateway(handler);

        var result = await gateway.CreatePullRequestAsync(
            Request(), TestContext.Current.CancellationToken);

        Assert.Equal(CodePublicationCodes.HeadBranchMissing, result.Code);
        Assert.Single(handler.Methods);
    }

    /// <summary>
    /// Two pull requests for one derived head is a state a person resolves. The head is created only by
    /// a governed push, so there is no correct way to pick one.
    /// </summary>
    [Fact]
    public async Task TwoPullRequestsForOneHeadRefuseRatherThanBeingPickedFrom()
    {
        var handler = new PullRequestHandler(Standard(
            listing: [Existing(17, HeadCommit), Existing(18, HeadCommit)]));
        using var gateway = CreateGateway(handler);

        var result = await gateway.CreatePullRequestAsync(
            Request(), TestContext.Current.CancellationToken);

        Assert.Equal(CodePublicationCodes.PullRequestAmbiguous, result.Code);
        Assert.DoesNotContain(handler.Methods, static method => method == HttpMethod.Post);
    }

    /// <summary>
    /// A pull request the provider returned for a different base is not this product's, whatever the
    /// query asked for.
    /// </summary>
    [Fact]
    public async Task APullRequestAgainstAnotherBaseIsNeverAdopted()
    {
        var handler = new PullRequestHandler(Standard(
            listing: [Existing(17, HeadCommit, baseRef: "release")]));
        using var gateway = CreateGateway(handler);

        var result = await gateway.CreatePullRequestAsync(
            Request(), TestContext.Current.CancellationToken);

        Assert.Equal(CodePublicationCodes.PullRequestMismatch, result.Code);
        Assert.DoesNotContain(handler.Methods, static method => method == HttpMethod.Post);
    }

    /// <summary>
    /// A create whose answer never arrived, and a create the provider could not complete, are both
    /// unknown outcomes: the pull request may exist. A rejection the provider stated is not.
    /// </summary>
    [Theory]
    [InlineData(null, CodePublicationCodes.OutcomeUnknown)]
    [InlineData(HttpStatusCode.ServiceUnavailable, CodePublicationCodes.OutcomeUnknown)]
    [InlineData(HttpStatusCode.UnprocessableEntity, CodePublicationCodes.RequestInvalid)]
    [InlineData(HttpStatusCode.Forbidden, CodePublicationCodes.Forbidden)]
    public async Task ACreateThatDidNotAnswerIsUnknownAndOneThatWasRejectedIsNot(
        HttpStatusCode? status,
        string expected)
    {
        var handler = new PullRequestHandler(request => IsPullCreate(request)
            ? status is null
                ? throw new HttpRequestException("connection reset")
                : new HttpResponseMessage(status.Value)
                {
                    Content = new StringContent("provider-body-secret-sentinel")
                }
            : Standard()(request));
        using var gateway = CreateGateway(handler);

        var result = await gateway.CreatePullRequestAsync(
            Request(), TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Code);
        Assert.Null(result.Number);
        Assert.Equal(1, handler.Methods.Count(static method => method == HttpMethod.Post));
    }

    /// <summary>
    /// An outcome that was never known is durable, and it settles on the one listing read the next
    /// attempt makes before it would create anything. The first attempt here loses its answer; the
    /// second finds the pull request that attempt in fact opened, and sends no create.
    /// </summary>
    [Fact]
    public async Task AnUnknownOutcomeIsSettledByTheOneReadThatPrecedesTheNextCreate()
    {
        var lost = new PullRequestHandler(request => IsPullCreate(request)
            ? throw new HttpRequestException("connection reset")
            : Standard()(request));
        using var lostGateway = CreateGateway(lost);

        var unknown = await lostGateway.CreatePullRequestAsync(
            Request(), TestContext.Current.CancellationToken);

        Assert.Equal(CodePublicationCodes.OutcomeUnknown, unknown.Code);
        Assert.Null(unknown.Number);

        var settling = new PullRequestHandler(Standard(existing: Existing(17, HeadCommit)));
        using var settlingGateway = CreateGateway(settling);

        var settled = await settlingGateway.CreatePullRequestAsync(
            Request(), TestContext.Current.CancellationToken);

        Assert.Equal(CodePublicationCodes.PullRequestAlreadyOpen, settled.Code);
        Assert.Equal(17, settled.Number);
        Assert.Equal(1, settling.Paths.Count(static path =>
            path.Contains("/pulls?", StringComparison.Ordinal)));
        Assert.DoesNotContain(settling.Methods, static method => method == HttpMethod.Post);
    }

    [Theory]
    [InlineData("../../main")]
    [InlineData("name with spaces")]
    public async Task AHeadNameOutsideTheAllowlistNeverReachesARequest(string headBranch)
    {
        var handler = new PullRequestHandler(Standard());
        using var gateway = CreateGateway(handler);

        var result = await gateway.CreatePullRequestAsync(
            Request() with { HeadBranch = headBranch }, TestContext.Current.CancellationToken);

        Assert.Equal(CodePublicationCodes.RequestInvalid, result.Code);
        Assert.Empty(handler.Methods);
    }

    [Fact]
    public async Task AnUnconfiguredHostRefusesWithoutReachingTheProvider()
    {
        var handler = new PullRequestHandler(Standard());
        using var gateway = CreateGateway(handler, baseBranch: null);

        var result = await gateway.CreatePullRequestAsync(
            Request(), TestContext.Current.CancellationToken);

        Assert.Equal(CodePublicationCodes.BindingUnavailable, result.Code);
        Assert.Empty(handler.Methods);
    }

    /// <summary>
    /// The bound on public text, checked at the last place before bytes become a page.
    /// </summary>
    [Fact]
    public async Task AnOversizedTitleOrBodyNeverReachesARequest()
    {
        var handler = new PullRequestHandler(Standard());
        using var gateway = CreateGateway(handler);

        var result = await gateway.CreatePullRequestAsync(
            Request() with { Body = new string('x', 5000) }, TestContext.Current.CancellationToken);

        Assert.Equal(CodePublicationCodes.RequestInvalid, result.Code);
        Assert.Empty(handler.Methods);
    }

    private static CodePublicationPullRequestRequest Request() => new(
        HeadBranch, HeadCommit, "Governed remediation", "Composed by the backend.\n");

    private static GitHubCodePublicationGateway CreateGateway(
        HttpMessageHandler handler,
        string? baseBranch = "main") =>
        new(
            Options.Create(new GitHubIssuesOptions
            {
                Owner = "acme",
                Repository = "checkout",
                Token = Token,
                TimeoutSeconds = 10
            }),
            Options.Create(new GitHubCodePublicationOptions
            {
                BaseBranch = baseBranch,
                TimeoutSeconds = 10
            }),
            handler);

    private static object Existing(int number, string headSha, string baseRef = "main") => new
    {
        number,
        title = "provider-title-sentinel",
        body = "provider-body-sentinel",
        head = new { @ref = HeadBranch, sha = headSha },
        @base = new { @ref = baseRef }
    };

    private static Func<HttpRequestMessage, HttpResponseMessage> Standard(
        string headCommit = HeadCommit,
        object? existing = null,
        object[]? listing = null) =>
        request =>
        {
            if (IsRefRead(request))
            {
                return Json(HttpStatusCode.OK, new { @object = new { sha = headCommit } });
            }

            if (IsPullListing(request))
            {
                return Json(
                    HttpStatusCode.OK,
                    listing ?? (existing is null ? [] : new[] { existing }));
            }

            return IsPullCreate(request)
                ? Json(HttpStatusCode.Created, Existing(17, HeadCommit))
                : throw new InvalidOperationException(
                    "Unexpected provider call: " + request.RequestUri!.PathAndQuery);
        };

    private static bool IsRefRead(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get &&
        request.RequestUri!.AbsolutePath.Contains("/git/ref/heads/", StringComparison.Ordinal);

    private static bool IsPullListing(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get &&
        request.RequestUri!.AbsolutePath.EndsWith("/pulls", StringComparison.Ordinal);

    private static bool IsPullCreate(HttpRequestMessage request) =>
        request.Method == HttpMethod.Post &&
        request.RequestUri!.AbsolutePath.EndsWith("/pulls", StringComparison.Ordinal);

    private static HttpResponseMessage Json(HttpStatusCode statusCode, object body) =>
        new(statusCode)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

    private sealed class PullRequestHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
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
            Paths.Add(request.RequestUri!.PathAndQuery);
            Bodies.Add(request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return response(request);
        }
    }
}
