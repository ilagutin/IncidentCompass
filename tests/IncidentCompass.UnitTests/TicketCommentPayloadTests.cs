using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Infrastructure.Tickets;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The one frozen shape a governed issue comment is approved in, now that it carries a link.
/// </summary>
/// <remarks>
/// The contract changed deliberately and in the three places that enforce it. These tests hold the
/// change to what it was supposed to be: one more property, required to be null for the comment a
/// report always gets and a number for the backlink it gets when its chain opened a pull request, with
/// neither tool able to accept the other's value.
/// </remarks>
public sealed class TicketCommentPayloadTests
{
    private static readonly Guid ReportId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void TheCommentContractIsSixPropertiesAtVersionTwo()
    {
        using var document = JsonDocument.Parse(Prepare(update: true).CanonicalPayload);

        Assert.Equal(
            GitHubIssueCommentMarker.PropertyCount,
            document.RootElement.EnumerateObject().Count());
        Assert.Equal(
            GitHubIssueCommentMarker.SchemaVersion,
            document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            JsonValueKind.Null, document.RootElement.GetProperty("pullRequestNumber").ValueKind);
    }

    [Fact]
    public void TheBacklinkStatesThePullRequestAndTheAbsenceOfATest()
    {
        using var document = JsonDocument.Parse(Prepare(update: false).CanonicalPayload);
        var body = document.RootElement.GetProperty("body").GetString()!;

        Assert.Equal("17", document.RootElement.GetProperty("pullRequestNumber").GetString());
        Assert.Contains("#17", body, StringComparison.Ordinal);
        Assert.Contains(ReportId.ToString("N"), body, StringComparison.Ordinal);
        Assert.Contains("No test was executed", body, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Neither tool can be used as the other. An evidence comment carrying a number is refused, and a
    /// backlink carrying none is refused, so the new field cannot turn one approval into the other.
    /// </summary>
    [Fact]
    public void NeitherToolAcceptsTheOthersArguments()
    {
        using var update = CreateUpdateTool();
        using var backlink = CreateBacklinkTool();

        Assert.False(update.Validate(Arguments(update: true, pullRequest: "17")).IsValid);
        Assert.False(backlink.Validate(Arguments(update: false, pullRequest: null)).IsValid);
        Assert.False(update.Validate(Arguments(update: false, pullRequest: "17")).IsValid);
        Assert.False(backlink.Validate(Arguments(update: true, pullRequest: "17")).IsValid);
    }

    /// <summary>
    /// The two comments do not suppress each other. The marker covers the proposal key, which carries
    /// the tool id, so one report leaves at most one of each kind on a ticket rather than one in total.
    /// </summary>
    [Fact]
    public void TheEvidenceCommentAndTheBacklinkHaveDifferentMarkers()
    {
        Assert.NotEqual(Marker(Prepare(update: true)), Marker(Prepare(update: false)));
    }

    /// <summary>
    /// The stated cost of the change: a comment frozen under version 1 is no longer executable, and it
    /// fails closed rather than being read as something it is not.
    /// </summary>
    [Fact]
    public void AVersionOnePayloadIsNoLongerReadable()
    {
        var body = Node(Prepare(update: true));
        body.Remove("pullRequestNumber");
        body["schemaVersion"] = 1;

        Assert.False(GitHubIssueCommentMarker.TryReadPayload(Canonical(body), out _));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("017")]
    [InlineData("")]
    [InlineData("17 ")]
    public void APullRequestNumberOutsideTheProvidersOwnSpellingIsRefused(string value)
    {
        var body = Node(Prepare(update: false));
        body["pullRequestNumber"] = value;

        Assert.False(GitHubIssueCommentMarker.TryReadPayload(Canonical(body), out _));
    }

    [Fact]
    public void APayloadCarryingASeventhPropertyIsRefused()
    {
        var body = Node(Prepare(update: true));
        body["merge"] = true;

        Assert.False(GitHubIssueCommentMarker.TryReadPayload(Canonical(body), out _));
    }

    private static string Marker(Application.Governance.Tools.ExternalActionPreparation prepared)
    {
        using var document = JsonDocument.Parse(prepared.CanonicalPayload);
        return document.RootElement.GetProperty("marker").GetString()!;
    }

    private static JsonObject Node(Application.Governance.Tools.ExternalActionPreparation prepared) =>
        JsonNode.Parse(prepared.CanonicalPayload)!.AsObject();

    private static byte[] Canonical(JsonObject body) =>
        Encoding.UTF8.GetBytes(CanonicalJsonSerializer.Canonicalize(body));

    private static Application.Governance.Tools.ExternalActionPreparation Prepare(bool update)
    {
        if (update)
        {
            using var tool = CreateUpdateTool();
            var validation = tool.Validate(Arguments(update: true, pullRequest: null));
            Assert.True(validation.IsValid);
            return tool.Prepare(validation.SanitizedArguments);
        }

        using var backlink = CreateBacklinkTool();
        var backlinkValidation = backlink.Validate(Arguments(update: false, pullRequest: "17"));
        Assert.True(backlinkValidation.IsValid);
        return backlink.Prepare(backlinkValidation.SanitizedArguments);
    }

    private static JsonElement Arguments(bool update, string? pullRequest)
    {
        var toolId = update
            ? TicketUpdatePostReportActionWorkflow.UpdateToolId
            : TicketBacklinkDescriptor.ToolId;
        var arguments = new JsonObject
        {
            ["originReportId"] = ReportId.ToString("N"),
            ["proposalKey"] = $"post-report:v1:{ReportId:N}:{toolId}",
            ["ticketId"] = "42"
        };
        if (pullRequest is not null)
        {
            arguments["pullRequestNumber"] = pullRequest;
        }

        return CanonicalJsonSerializer.ToElement(arguments);
    }

    private static GitHubIssueCommentExternalActionTool CreateUpdateTool() =>
        new(Microsoft.Extensions.Options.Options.Create(Options()), new UnreachableHandler());

    private static GitHubIssueBacklinkExternalActionTool CreateBacklinkTool() =>
        new(Microsoft.Extensions.Options.Options.Create(Options()), new UnreachableHandler());

    private static GitHubIssuesOptions Options() => new()
    {
        Owner = "owner",
        Repository = "repo",
        Token = "ticket-comment-token-sentinel",
        TimeoutSeconds = 10
    };

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("No provider call is expected from a payload test.");
    }
}
