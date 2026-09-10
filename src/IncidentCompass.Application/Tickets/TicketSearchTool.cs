using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Governance.Validation;
using IncidentCompass.Domain.Governance;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.Tickets;

internal sealed class TicketSearchTool(ITicketSearch ticketSearch) : IImmediateAgentTool
{
    public AiToolDefinition Definition { get; } = new(
        "ticket_search",
        "Search configured read-only ticket context using backend-owned incident fields.",
        "v1",
        CanonicalJsonSerializer.ToElement(new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject()
        }));

    public ToolValidationResult Validate(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any())
        {
            return ToolValidationResult.Invalid("invalid_arguments", "ticket_search accepts only an empty object.");
        }

        return ToolValidationResult.Valid(CanonicalJsonSerializer.ToElement(new JsonObject()));
    }

    public async Task<ToolExecutionResult> ExecuteAsync(
        AgentToolExecutionContext context,
        JsonElement sanitizedArguments,
        CancellationToken cancellationToken)
    {
        if (context.TriggerSignal is null)
        {
            return Successful(TicketSearchResult.Unavailable("ticket_search_signal_unavailable"));
        }

        var request = TicketSearchContextExtractor.Create(
            context.FaultFingerprint,
            context.FaultServiceName,
            context.TriggerSignal);
        var result = await ticketSearch.SearchAsync(request, cancellationToken);
        return Successful(result);
    }

    private static ToolExecutionResult Successful(TicketSearchResult result)
    {
        var drafts = result.Matches.Select(CreateDraft).ToArray();
        return new ToolExecutionResult(
            ToolExecutionStatus.Succeeded,
            CreateOutput(result, drafts),
            Artifacts: drafts);
    }

    private static ToolArtifactDraft CreateDraft(TicketSearchMatch match)
    {
        _ = int.TryParse(match.ExternalId, out var issueNumber);
        var payload = new JsonObject
        {
            ["evidenceKind"] = "ExistingTicket",
            ["provider"] = match.Provider,
            ["repository"] = match.Scope,
            ["issueNumber"] = issueNumber,
            ["title"] = match.Title,
            ["status"] = match.Status,
            ["assignee"] = match.Assignee,
            ["createdAtUtc"] = match.CreatedAtUtc.ToUniversalTime().ToString("O"),
            ["url"] = match.Url,
            ["score"] = match.Score
        };
        return new ToolArtifactDraft(
            ArtifactKind.RetrievedItem,
            $"ticket:{match.Provider}:{match.Scope}:{match.ExternalId}",
            payload);
    }

    private static JsonElement CreateOutput(TicketSearchResult result, ToolArtifactDraft[] drafts)
    {
        var items = new JsonArray();
        for (var index = 0; index < result.Matches.Count; index++)
        {
            var match = result.Matches[index];
            items.Add(new JsonObject
            {
                ["artifactId"] = drafts[index].Id.ToString(),
                ["provider"] = match.Provider,
                ["scope"] = match.Scope,
                ["externalId"] = match.ExternalId,
                ["title"] = match.Title,
                ["status"] = match.Status,
                ["assignee"] = match.Assignee,
                ["createdAtUtc"] = match.CreatedAtUtc.ToUniversalTime().ToString("O"),
                ["url"] = match.Url,
                ["score"] = match.Score
            });
        }

        var outcome = result.Outcome switch
        {
            TicketSearchOutcome.Matched => "matched",
            TicketSearchOutcome.NoMatch => "no_match",
            _ => "connector_unavailable"
        };
        return CanonicalJsonSerializer.ToElement(new JsonObject
        {
            ["matched"] = result.Outcome == TicketSearchOutcome.Matched && items.Count > 0,
            ["message"] = result.Outcome == TicketSearchOutcome.Matched ? "matches found" :
                result.Outcome == TicketSearchOutcome.NoMatch ? "no matches" : "connector unavailable",
            ["outcome"] = outcome,
            ["provider"] = result.Provider,
            ["repository"] = result.Repository,
            ["code"] = result.Code,
            ["items"] = items,
            ["noMatchReason"] = result.Outcome == TicketSearchOutcome.Matched ? null : result.Code
        });
    }
}
