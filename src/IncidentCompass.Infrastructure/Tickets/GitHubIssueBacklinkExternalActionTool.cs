using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Governance.Validation;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Domain.Incidents.Actions;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Tickets;

/// <summary>
/// The governed comment that tells a cited ticket which pull request now proposes a change for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is the evidence comment's machinery under a second id.</b> The same marker, the same
/// preflight, the same bound, the same delivery path and the same payload shape. What differs is that
/// this one requires a pull-request number where the other requires its absence, and that its intent is
/// written by the transaction that records a pull request as opened rather than at report publication.
/// The second id exists because the queue and the approval table each hold one row per report per tool,
/// so a report's single <c>ticket_update</c> is frozen long before any pull request exists.
/// </para>
/// <para>
/// <b>It still writes to an issue.</b> The preflight refuses any target the provider reports as a pull
/// request, and the evidence shape that resolved the target required an issue URL in the configured
/// repository. Neither was loosened: the link points at the pull request from the issue, which is the
/// only direction that was ever wanted.
/// </para>
/// <para>
/// <b>No model can name it, and no model wrote any of it.</b> It is an external action, so it never
/// reaches a model surface, and its body is composed from a report identifier and a number.
/// </para>
/// </remarks>
public sealed class GitHubIssueBacklinkExternalActionTool : IExternalActionTool, IDisposable
{
    private readonly GitHubIssuesOptions options;
    private readonly HttpClient client;

    public GitHubIssueBacklinkExternalActionTool(
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

    public string LogicalTargetId => TicketBacklinkDescriptor.LogicalTargetId;

    public string AdapterBindingFingerprint { get; }

    public AiToolDefinition Definition { get; } = new(
        TicketBacklinkDescriptor.ToolId,
        "Add one backend-governed comment linking a cited ticket to the governed pull request.",
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
                ["pullRequestNumber"] = new JsonObject
                {
                    ["type"] = "string",
                    ["pattern"] = "^[1-9][0-9]{0,9}$"
                },
                ["ticketId"] = new JsonObject { ["type"] = "string", ["pattern"] = "^[1-9][0-9]{0,9}$" }
            },
            ["required"] = new JsonArray(
                "originReportId", "proposalKey", "pullRequestNumber", "ticketId")
        }));

    public ToolValidationResult Validate(JsonElement arguments) =>
        options.IsConfigured
            ? GitHubIssueCommentPayloadFactory.Validate(
                arguments, TicketBacklinkDescriptor.ToolId, requiresPullRequest: true)
            : ToolValidationResult.Invalid(
                "invalid_arguments", "Ticket backlink arguments are invalid.");

    public ExternalActionPreparation Prepare(JsonElement sanitizedArguments) =>
        GitHubIssueCommentPayloadFactory.Prepare(sanitizedArguments, TicketBacklinkDescriptor.ToolId);

    public Task<ExternalActionExecutionResult> ExecuteAsync(
        Guid actionId,
        ReadOnlyMemory<byte> canonicalPayload,
        CancellationToken cancellationToken) =>
        GitHubIssueCommentDelivery.ExecuteAsync(client, options, canonicalPayload, cancellationToken);

    public void Dispose() => client.Dispose();
}
