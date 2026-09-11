using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Governance.Validation;
using IncidentCompass.Domain.Incidents.Actions;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Tickets;

/// <summary>
/// The governed evidence comment one report leaves on the one existing ticket it cited.
/// </summary>
/// <remarks>
/// It carries no pull request. The field exists in the shared payload and this tool requires it to be
/// null, so approving an evidence comment can never turn into approving a link to something, and the
/// comment a report gets today is byte-for-byte the comment it got before the backlink existed.
/// </remarks>
public sealed class GitHubIssueCommentExternalActionTool : IExternalActionTool, IDisposable
{
    private readonly GitHubIssuesOptions options;
    private readonly HttpClient client;

    public GitHubIssueCommentExternalActionTool(
        IOptions<GitHubIssuesOptions> options,
        HttpMessageHandler? handler = null)
    {
        this.options = options.Value;
        client = new HttpClient(handler ?? GitHubIssuesHttpMessageHandlerFactory.Create(), disposeHandler: true)
        {
            BaseAddress = GitHubIssuesTicketSearch.Authority,
            Timeout = Timeout.InfiniteTimeSpan
        };
        AdapterBindingFingerprint = ExternalActionBinding.ComputeFingerprint(
            "github-issues",
            LogicalTargetId,
            GitHubIssuesTicketSearch.Authority.AbsoluteUri,
            this.options.ConfiguredRepository ?? "unconfigured");
    }

    public ActionCategory Category => ActionCategory.TicketUpdate;

    public string LogicalTargetId => TicketUpdatePostReportActionWorkflow.UpdateLogicalTargetId;

    public string AdapterBindingFingerprint { get; }

    public AiToolDefinition Definition { get; } = new(
        TicketUpdatePostReportActionWorkflow.UpdateToolId,
        "Add one backend-governed evidence comment to one cited ticket.",
        "v1",
        CanonicalJsonSerializer.ToElement(new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["originReportId"] = new JsonObject
                {
                    ["type"] = "string",
                    ["pattern"] = "^[0-9a-f]{32}$"
                },
                ["proposalKey"] = new JsonObject { ["type"] = "string" },
                ["ticketId"] = new JsonObject { ["type"] = "string", ["pattern"] = "^[1-9][0-9]{0,9}$" }
            },
            ["required"] = new JsonArray("originReportId", "proposalKey", "ticketId")
        }));

    public ToolValidationResult Validate(JsonElement arguments) =>
        options.IsConfigured
            ? GitHubIssueCommentPayloadFactory.Validate(
                arguments,
                TicketUpdatePostReportActionWorkflow.UpdateToolId,
                requiresPullRequest: false)
            : ToolValidationResult.Invalid(
                "invalid_arguments", "Ticket update arguments are invalid.");

    public ExternalActionPreparation Prepare(JsonElement sanitizedArguments) =>
        GitHubIssueCommentPayloadFactory.Prepare(
            sanitizedArguments, TicketUpdatePostReportActionWorkflow.UpdateToolId);

    public Task<ExternalActionExecutionResult> ExecuteAsync(
        Guid actionId,
        ReadOnlyMemory<byte> canonicalPayload,
        CancellationToken cancellationToken) =>
        GitHubIssueCommentDelivery.ExecuteAsync(client, options, canonicalPayload, cancellationToken);

    public void Dispose() => client.Dispose();
}
