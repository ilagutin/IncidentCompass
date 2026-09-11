using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Application.Notifications;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Domain.Incidents.Actions;

namespace IncidentCompass.Application.Governance.ActionApprovals;

public static class ActionTerminalValidator
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void Validate(ActionTerminalRequest request)
    {
        if (request.TerminalState is not (ActionApprovalState.Executed or ActionApprovalState.Failed) ||
            request.ResultPayload.Length is < 1 or > ActionApprovalLimits.MaximumResultBytes ||
            StrictUtf8.GetByteCount(request.ResultSummary) is < 1 or > ActionApprovalLimits.MaximumSummaryBytes ||
            (request.TerminalState == ActionApprovalState.Failed) != !string.IsNullOrWhiteSpace(request.FailureCode) ||
            request.FailureCode?.Length > 128 ||
            request.AuditProjection is not null && request.TerminalState != ActionApprovalState.Executed)
        {
            throw new ActionProposalValidationException("Action terminal result is invalid or exceeds its bound.");
        }

        request.AuditProjection?.Validate();

        try
        {
            var text = StrictUtf8.GetString(request.ResultPayload);
            var node = JsonNode.Parse(text) ?? throw new JsonException();
            if (!request.ResultPayload.AsSpan().SequenceEqual(
                    Encoding.UTF8.GetBytes(CanonicalJsonSerializer.Canonicalize(node))))
            {
                throw new ActionProposalValidationException("Action terminal result must be canonical JSON.");
            }
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            throw new ActionProposalValidationException("Action terminal result must be valid canonical UTF-8 JSON.", exception);
        }
    }

    public static void ValidateForAction(
        ActionApprovalRecord action,
        ActionTerminalRequest request)
    {
        Validate(request);
        var supportedCategory = SupportedCategory(action.ToolId);
        if (supportedCategory is not null && supportedCategory != action.Category)
        {
            throw InvalidProjection();
        }

        var requiresProjection = request.TerminalState == ActionApprovalState.Executed &&
            action.Mode == ActionExecutionMode.Live &&
            supportedCategory == action.Category;
        if ((request.AuditProjection is not null) != requiresProjection ||
            request.AuditProjection is not null && !request.AuditProjection.Matches(action.Category))
        {
            throw InvalidProjection();
        }
    }

    private static ActionCategory? SupportedCategory(string toolId) => toolId switch
    {
        TelegramNotificationWorkflow.ToolIdValue => ActionCategory.Notification,
        TicketCreateTool.ToolId => ActionCategory.TicketCreate,
        TicketUpdatePostReportActionWorkflow.UpdateToolId => ActionCategory.TicketUpdate,
        BranchPushToolDescriptor.ToolId => ActionCategory.BranchPush,
        PullRequestToolDescriptor.ToolId => ActionCategory.PrCreate,
        TicketBacklinkDescriptor.ToolId => ActionCategory.TicketUpdate,
        _ => null
    };

    private static ActionProposalValidationException InvalidProjection() =>
        new("External action audit projection does not match the claimed action.");
}
