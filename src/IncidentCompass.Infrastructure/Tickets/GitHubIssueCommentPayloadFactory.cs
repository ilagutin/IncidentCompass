using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Governance.Validation;

namespace IncidentCompass.Infrastructure.Tickets;

/// <summary>
/// Builds and validates the one frozen shape a governed issue comment is approved in, for both the
/// comment a report always gets and the backlink a report gets when its chain opened a pull request.
/// </summary>
/// <remarks>
/// <para>
/// <b>One shape, two tools, and the difference is a single field.</b> Both comments are the same write
/// to the same kind of target, so they share the payload, the marker, the preflight and the bound. What
/// differs is whether <c>pullRequestNumber</c> is a number or null, and each tool requires exactly one
/// of the two: a plain comment carrying a number is refused, and a backlink carrying null is refused.
/// So the field cannot be used to make one tool behave as the other, and the shape is enforced in the
/// same three places it always was - here in validation, again in the payload reader, and again in the
/// declared argument schema.
/// </para>
/// <para>
/// <b>Every comment body is composed here and nowhere else.</b> The only values that reach one are a
/// report identifier and a pull-request number, both of fixed shape. No ticket title, no report text,
/// no diff and nothing a model wrote is a parameter of this type.
/// </para>
/// </remarks>
internal static class GitHubIssueCommentPayloadFactory
{
    public static ToolValidationResult Validate(
        JsonElement arguments,
        string toolId,
        bool requiresPullRequest)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            arguments.EnumerateObject().Any(property =>
                !IsKnownArgument(property.Name, requiresPullRequest)) ||
            !arguments.TryGetProperty("originReportId", out var report) ||
            report.ValueKind != JsonValueKind.String ||
            !Guid.TryParseExact(report.GetString(), "N", out var reportId) ||
            !arguments.TryGetProperty("proposalKey", out var key) || key.ValueKind != JsonValueKind.String ||
            !string.Equals(
                key.GetString(), ProposalKey(reportId, toolId), StringComparison.Ordinal) ||
            !arguments.TryGetProperty("ticketId", out var ticket) ||
            ticket.ValueKind != JsonValueKind.String ||
            !GitHubIssueCommentMarker.TryReadNumber(ticket.GetString(), int.MaxValue, out var issueNumber) ||
            !TryReadPullRequestArgument(arguments, requiresPullRequest, out var pullRequest))
        {
            return ToolValidationResult.Invalid(
                "invalid_arguments", "Ticket comment arguments are invalid.");
        }

        var sanitized = new JsonObject
        {
            ["originReportId"] = reportId.ToString("N"),
            ["proposalKey"] = key.GetString(),
            ["ticketId"] = Text(issueNumber)
        };
        if (requiresPullRequest)
        {
            sanitized["pullRequestNumber"] = pullRequest;
        }

        return ToolValidationResult.Valid(CanonicalJsonSerializer.ToElement(sanitized));
    }

    public static ExternalActionPreparation Prepare(JsonElement sanitizedArguments, string toolId)
    {
        var reportId = Guid.ParseExact(
            sanitizedArguments.GetProperty("originReportId").GetString()!, "N");
        var proposalKey = sanitizedArguments.GetProperty("proposalKey").GetString()!;
        var ticketId = sanitizedArguments.GetProperty("ticketId").GetString()!;
        var pullRequest = sanitizedArguments.TryGetProperty("pullRequestNumber", out var number)
            ? number.GetString()
            : null;
        var marker = GitHubIssueCommentMarker.Create(proposalKey, reportId, ticketId);
        var payload = new JsonObject
        {
            ["body"] = Body(reportId, pullRequest, marker),
            ["marker"] = marker,
            ["originReportId"] = reportId.ToString("N"),
            ["pullRequestNumber"] = pullRequest,
            ["schemaVersion"] = GitHubIssueCommentMarker.SchemaVersion,
            ["ticketId"] = ticketId
        };
        return new ExternalActionPreparation(
            Encoding.UTF8.GetBytes(CanonicalJsonSerializer.Canonicalize(payload)),
            Summary(reportId, ticketId, pullRequest));
    }

    public static string ProposalKey(Guid originReportId, string toolId) =>
        $"post-report:v1:{originReportId:N}:{toolId}";

    private static bool IsKnownArgument(string name, bool requiresPullRequest) =>
        name is "originReportId" or "proposalKey" or "ticketId" ||
        (requiresPullRequest && name == "pullRequestNumber");

    private static bool TryReadPullRequestArgument(
        JsonElement arguments,
        bool requiresPullRequest,
        out string? value)
    {
        value = null;
        if (!requiresPullRequest)
        {
            return true;
        }

        if (!arguments.TryGetProperty("pullRequestNumber", out var property) ||
            property.ValueKind != JsonValueKind.String ||
            !GitHubIssueCommentMarker.TryReadNumber(
                property.GetString(),
                GitHubIssueCommentMarker.MaximumPullRequestNumber,
                out var number))
        {
            return false;
        }

        value = Text(number);
        return true;
    }

    private static string Body(Guid reportId, string? pullRequest, string marker)
    {
        var builder = new StringBuilder("Governed incident report update: ")
            .Append(reportId.ToString("N")).Append(".\n\n");
        if (pullRequest is null)
        {
            builder.Append("Review the report and its cited evidence before acting.\n\n");
        }
        else
        {
            builder.Append("A governed pull request now proposes a change for this incident: #")
                .Append(pullRequest).Append(".\n\n")
                .Append("No test was executed for that change. Review the report, its cited evidence ")
                .Append("and the pull request's own description before acting.\n\n");
        }

        return builder.Append(GitHubIssueCommentMarker.Comment(marker)).ToString();
    }

    private static string Summary(Guid reportId, string ticketId, string? pullRequest) =>
        pullRequest is null
            ? $"Add one evidence comment to ticket {ticketId} for incident report {reportId:N}."
            : $"Add one comment to ticket {ticketId} linking incident report {reportId:N} to pull " +
                $"request {pullRequest}. Nothing is merged and no test was executed.";

    private static string Text(int value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
