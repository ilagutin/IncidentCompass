using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.Tools;

namespace IncidentCompass.Infrastructure.Tickets;

/// <summary>
/// The provider half of a governed issue comment, shared by the comment a report always gets and the
/// backlink it gets when its chain opened a pull request.
/// </summary>
/// <remarks>
/// <para>
/// Both comments are the same write to the same kind of target under the same at-most-once rules, so
/// they share one delivery path rather than two that could drift. In particular they share the
/// preflight, which refuses any target the provider reports as a pull request. That refusal is what
/// stops a governed comment from being posted into a conversation this product opened; a backlink is
/// exactly the case that might have tempted someone to relax it, and it does not, because a backlink
/// still lands on the issue and only mentions the pull request.
/// </para>
/// <para>
/// Nothing here composes text. The body arrives frozen in the approval and is sent byte for byte.
/// </para>
/// </remarks>
internal static class GitHubIssueCommentDelivery
{
    public static async Task<ExternalActionExecutionResult> ExecuteAsync(
        HttpClient client,
        GitHubIssuesOptions options,
        ReadOnlyMemory<byte> canonicalPayload,
        CancellationToken cancellationToken)
    {
        if (!options.IsConfigured)
        {
            return GitHubIssueCommentResponseParser.Failure("github_issue_comment_binding_unavailable");
        }

        if (!GitHubIssueCommentMarker.TryReadPayload(canonicalPayload, out var payload))
        {
            return GitHubIssueCommentResponseParser.Failure("github_issue_comment_payload_invalid");
        }

        var preflight = await GitHubIssueCommentPreflight.SafeCheckAsync(
            client, options, payload.IssueNumber, payload.Marker, cancellationToken);
        if (preflight.FailureCode is not null)
        {
            return GitHubIssueCommentResponseParser.Failure(preflight.FailureCode);
        }

        if (preflight.Existing is not null)
        {
            return preflight.Existing;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return GitHubIssueCommentResponseParser.Failure(
                "github_issue_comment_cancelled_before_write");
        }

        using var request = CreateRequest(options, payload.IssueNumber, payload.Body);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            return await GitHubIssueCommentResponseParser.ParseCreateAsync(
                response, options, payload.IssueNumber, payload.Marker, deadline.Token);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or HttpRequestException or IOException or JsonException)
        {
            return GitHubIssueCommentResponseParser.Failure("dispatch_outcome_unknown");
        }
    }

    private static HttpRequestMessage CreateRequest(
        GitHubIssuesOptions options,
        int issueNumber,
        string body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/repos/{options.Owner}/{options.Repository}/issues/{issueNumber}/comments")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(
                CanonicalJsonSerializer.Canonicalize(new JsonObject { ["body"] = body })))
        };
        GitHubIssueCreateRequestFactory.AddGitHubHeaders(request, options.Token!);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };
        return request;
    }
}
