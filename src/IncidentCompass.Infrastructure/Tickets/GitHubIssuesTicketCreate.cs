using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Governance.Validation;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Domain.Incidents.Actions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Tickets;

public sealed partial class GitHubIssuesTicketCreate : IExternalActionTool, IDisposable
{
    private readonly GitHubIssuesOptions options;
    private readonly ITicketActionHistory history;
    private readonly HttpClient client;
    private readonly ILogger logger;
    public GitHubIssuesTicketCreate(
        IOptions<GitHubIssuesOptions> options,
        ITicketActionHistory history,
        HttpMessageHandler? handler = null,
        ILogger<GitHubIssuesTicketCreate>? logger = null)
    {
        this.options = options.Value;
        this.history = history;
        this.logger = logger ?? NullLogger<GitHubIssuesTicketCreate>.Instance;
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

    public ActionCategory Category => ActionCategory.TicketCreate;
    public string LogicalTargetId => TicketCreateTool.LogicalTargetId;
    public string AdapterBindingFingerprint { get; }
    public AiToolDefinition Definition { get; } = new(
        TicketCreateTool.ToolId,
        "Create one backend-governed issue in the fixed ticket repository.",
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
                ["proposalKey"] = new JsonObject { ["type"] = "string" }
            },
            ["required"] = new JsonArray("originReportId", "proposalKey")
        }));
    public ToolValidationResult Validate(JsonElement arguments)
    {
        if (!options.IsConfigured || arguments.ValueKind != JsonValueKind.Object ||
            arguments.EnumerateObject().Any(static property =>
                property.Name is not ("originReportId" or "proposalKey")) ||
            !arguments.TryGetProperty("originReportId", out var report) ||
            report.ValueKind != JsonValueKind.String ||
            !Guid.TryParseExact(report.GetString(), "N", out var reportId) ||
            !arguments.TryGetProperty("proposalKey", out var key) ||
            key.ValueKind != JsonValueKind.String ||
            !string.Equals(key.GetString(),
                $"post-report:v1:{reportId:N}:{TicketCreateTool.ToolId}",
                StringComparison.Ordinal))
        {
            return ToolValidationResult.Invalid(
                "invalid_arguments", "Ticket create arguments are invalid.");
        }

        return ToolValidationResult.Valid(JsonSerializer.SerializeToElement(new
        {
            originReportId = reportId.ToString("N"),
            proposalKey = key.GetString()
        }));
    }

    public ExternalActionPreparation Prepare(JsonElement sanitizedArguments) =>
        TicketCreatePayloadFactory.Create(
            Guid.ParseExact(sanitizedArguments.GetProperty("originReportId").GetString()!, "N"),
            sanitizedArguments.GetProperty("proposalKey").GetString()!);

    public async Task<ExternalActionExecutionResult> ExecuteAsync(
        Guid actionId,
        ReadOnlyMemory<byte> canonicalPayload,
        CancellationToken cancellationToken)
    {
        if (!options.IsConfigured)
        {
            return Failure("github_issue_binding_unavailable");
        }
        string currentMarker;
        try
        {
            currentMarker = GitHubIssueCreateRequestFactory.ReadMarker(canonicalPayload);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return Failure("github_issue_payload_invalid");
        }
        var prior = await history.ReadPriorAsync(actionId, cancellationToken);
        if (prior.HistoryLimitExceeded)
        {
            return Failure("github_issue_history_exceeded");
        }
        if (prior.HasUnsafeHistory)
        {
            return Failure("github_issue_history_invalid");
        }
        if (prior.ConfirmedCanonicalResult is not null)
        {
            if (!GitHubIssueCreateResponseParser.TryValidateCanonicalResult(
                    prior.ConfirmedCanonicalResult, out var confirmed, out var issueNumber))
            {
                return Failure("github_issue_prior_result_invalid");
            }
            return new ExternalActionExecutionResult(
                true,
                confirmed,
                "A prior governed action already created the GitHub issue.",
                AuditProjection: ExternalActionAuditProjection.GitHubIssueCreated(issueNumber));
        }
        if (prior.HasPendingAction)
        {
            return Failure("github_issue_prior_action_pending");
        }
        foreach (var marker in prior.OutcomeUnknownMarkers.Append(currentMarker).Distinct(StringComparer.Ordinal))
        {
            var preflight = await GitHubIssuePreflightSearch.SafeSearchAsync(
                client, options, marker, cancellationToken);
            if (preflight.FailureCode is not null)
            {
                return Failure(preflight.FailureCode);
            }
            if (preflight.Existing is not null)
            {
                return preflight.Existing;
            }
        }
        if (prior.OutcomeUnknownMarkers.Count > 0)
        {
            return Failure("github_issue_prior_outcome_unknown");
        }
        return await CreateOnceAsync(canonicalPayload, cancellationToken);
    }

    public void Dispose() => client.Dispose();

    private async Task<ExternalActionExecutionResult> CreateOnceAsync(
        ReadOnlyMemory<byte> canonicalPayload,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Failure("github_issue_cancelled_before_write");
        }

        using var request = GitHubIssueCreateRequestFactory.Create(canonicalPayload, options);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var result = await GitHubIssueCreateResponseParser.ParseCreateAsync(
                response, options, deadline.Token);
            if (!result.Succeeded)
            {
                LogProviderFailure(logger, result.FailureCode ?? "github_issue_create_failed");
            }

            return result;
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or HttpRequestException or IOException or JsonException)
        {
            return Failure("dispatch_outcome_unknown");
        }
    }

    private static ExternalActionExecutionResult Failure(string code) =>
        GitHubIssueCreateResponseParser.Failure(code);

    [LoggerMessage(
        EventId = 2501,
        Level = LogLevel.Warning,
        Message = "GitHub issue provider returned failure code {FailureCode}.")]
    private static partial void LogProviderFailure(ILogger logger, string failureCode);
}
