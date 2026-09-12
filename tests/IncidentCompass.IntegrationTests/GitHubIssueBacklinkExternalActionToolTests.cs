using System.Net;
using System.Text;
using System.Text.Json;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Infrastructure.Tickets;
using Microsoft.Extensions.Options;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// The governed backlink: one comment on the cited issue saying which pull request answers it.
/// </summary>
/// <remarks>
/// The two facts worth proving are that it lands exactly once and that it lands on an issue. The first
/// is the marker preflight the evidence comment already had, which the backlink shares rather than
/// reimplements. The second is the refusal that was in the way of the whole feature and was kept: the
/// preflight still declines any target the provider reports as a pull request, so a link to a pull
/// request never becomes a comment inside one.
/// </remarks>
public sealed class GitHubIssueBacklinkExternalActionToolTests
{
    private static readonly Guid ReportId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task AnEmptyPreflightMakesExactlyOneCommentCarryingThePullRequestNumber()
    {
        ExternalActionPreparation? prepared = null;
        var handler = new RecordingHandler(call => call switch
        {
            1 => Json(HttpStatusCode.OK, Issue(42)),
            2 => Json(HttpStatusCode.OK, Array.Empty<object>()),
            3 => Json(HttpStatusCode.Created, Comment(91, 42, Body(prepared!))),
            _ => throw new InvalidOperationException("Unexpected provider call.")
        });
        using var tool = CreateTool(handler);
        prepared = Payload(tool);

        var result = await tool.ExecuteAsync(
            Guid.NewGuid(), prepared.CanonicalPayload, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal([HttpMethod.Get, HttpMethod.Get, HttpMethod.Post], handler.Methods);
        Assert.Equal("/repos/owner/repo/issues/42/comments", handler.Paths[2]);
        Assert.Contains("#17", Body(prepared), StringComparison.Ordinal);
        Assert.Equal(ExternalActionAuditProjection.GitHubIssueKind, result.AuditProjection!.ResourceKind);
        Assert.Equal("42", result.AuditProjection.ResourceId);
        Assert.Equal("comment_added", result.AuditProjection.AfterState);
    }

    /// <summary>
    /// Exactly once: a replay finds the comment the first attempt left and writes nothing.
    /// </summary>
    [Fact]
    public async Task AReplayFindsTheBacklinkAlreadyThereAndWritesNothing()
    {
        ExternalActionPreparation? prepared = null;
        var handler = new RecordingHandler(call => call switch
        {
            1 => Json(HttpStatusCode.OK, Issue(42)),
            2 => Json(HttpStatusCode.OK, new[] { Comment(73, 42, Body(prepared!)) }),
            _ => throw new InvalidOperationException("Unexpected provider call.")
        });
        using var tool = CreateTool(handler);
        prepared = Payload(tool);

        var result = await tool.ExecuteAsync(
            Guid.NewGuid(), prepared.CanonicalPayload, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal([HttpMethod.Get, HttpMethod.Get], handler.Methods);
        Assert.Contains(
            "\"commentId\":\"73\"",
            Encoding.UTF8.GetString(result.CanonicalResult),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal the backlink was built around rather than through. A target the provider reports as
    /// a pull request is still refused, so the link goes on the issue and points at the pull request
    /// and never the other way.
    /// </summary>
    [Fact]
    public async Task ATargetThatIsAPullRequestIsStillRefusedAndNothingIsWritten()
    {
        var handler = new RecordingHandler(call => call == 1
            ? Json(HttpStatusCode.OK, new
            {
                number = 42,
                html_url = "https://github.com/owner/repo/issues/42",
                pull_request = new { url = "https://api.github.com/repos/owner/repo/pulls/42" }
            })
            : throw new InvalidOperationException("Unexpected provider call."));
        using var tool = CreateTool(handler);

        var result = await tool.ExecuteAsync(
            Guid.NewGuid(), Payload(tool).CanonicalPayload, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("github_issue_comment_preflight_malformed", result.FailureCode);
        Assert.DoesNotContain(handler.Methods, static method => method == HttpMethod.Post);
    }

    [Fact]
    public void ArgumentsCannotInjectATargetARepositoryOrAnotherToolsKey()
    {
        using var tool = CreateTool(new RecordingHandler(
            _ => throw new InvalidOperationException("No call expected.")));

        Assert.False(tool.Validate(JsonSerializer.SerializeToElement(new
        {
            originReportId = ReportId.ToString("N"),
            proposalKey = $"post-report:v1:{ReportId:N}:{TicketBacklinkDescriptor.ToolId}",
            pullRequestNumber = "17",
            ticketId = "42",
            repository = "attacker/repo"
        })).IsValid);
        Assert.False(tool.Validate(JsonSerializer.SerializeToElement(new
        {
            originReportId = ReportId.ToString("N"),
            proposalKey = $"post-report:v1:{ReportId:N}:ticket_update",
            pullRequestNumber = "17",
            ticketId = "42"
        })).IsValid);
    }

    private static GitHubIssueBacklinkExternalActionTool CreateTool(HttpMessageHandler handler) => new(
        Options.Create(new GitHubIssuesOptions
        {
            Owner = "owner",
            Repository = "repo",
            Token = "backlink-token-sentinel",
            TimeoutSeconds = 10
        }),
        handler);

    private static ExternalActionPreparation Payload(GitHubIssueBacklinkExternalActionTool tool)
    {
        var validation = tool.Validate(JsonSerializer.SerializeToElement(new
        {
            originReportId = ReportId.ToString("N"),
            proposalKey = $"post-report:v1:{ReportId:N}:{TicketBacklinkDescriptor.ToolId}",
            pullRequestNumber = "17",
            ticketId = "42"
        }));
        Assert.True(validation.IsValid);
        return tool.Prepare(validation.SanitizedArguments);
    }

    private static string Body(ExternalActionPreparation payload)
    {
        using var document = JsonDocument.Parse(payload.CanonicalPayload);
        return document.RootElement.GetProperty("body").GetString()!;
    }

    private static object Issue(int number) => new
    {
        number,
        html_url = $"https://github.com/owner/repo/issues/{number}"
    };

    private static object Comment(long id, int issueNumber, string body) => new
    {
        id,
        html_url = $"https://github.com/owner/repo/issues/{issueNumber}#issuecomment-{id}",
        issue_url = $"https://api.github.com/repos/owner/repo/issues/{issueNumber}",
        body
    };

    private static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(Func<int, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];

        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(responseFactory(Methods.Count));
        }
    }
}
