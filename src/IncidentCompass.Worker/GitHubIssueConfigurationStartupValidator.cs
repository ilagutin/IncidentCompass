using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Infrastructure.Tickets;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Worker;

internal sealed class GitHubIssueConfigurationStartupValidator(
    ITriageConfigurationRepository configurationRepository,
    IOptions<GitHubIssuesOptions> githubOptions) : IHostedService
{
    private const string BindingError =
        "GitHub issue host binding does not match an enabled public ticket action.";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var configuration = await configurationRepository.GetCurrentAsync(cancellationToken);
        var enabledTools = configuration.Actions.AllowedTools
            .Where(static toolId => toolId is TicketCreateTool.ToolId or
                TicketUpdatePostReportActionWorkflow.UpdateToolId or TicketBacklinkDescriptor.ToolId)
            .ToArray();
        if (enabledTools.Length == 0)
        {
            return;
        }

        foreach (var toolId in enabledTools)
        {
            if (!configuration.Tools.TryGetValue(toolId, out var tool) ||
                !string.Equals(tool.Kind, "external_action", StringComparison.Ordinal) ||
                !MatchesIdentity(toolId, tool))
            {
                throw new InvalidOperationException(BindingError);
            }

            if (!string.Equals(configuration.Actions.DefaultMode, "disabled", StringComparison.Ordinal) &&
                !string.Equals(tool.Mode, "disabled", StringComparison.Ordinal) &&
                !githubOptions.Value.IsConfigured)
            {
                throw new InvalidOperationException(BindingError);
            }
        }
    }

    /// <summary>
    /// The backlink is checked exactly as the evidence comment is: it is the same category on the same
    /// logical target, so a host that enables it and has no issue binding fails at boot rather than at
    /// the dispatch of an approval a person had already granted.
    /// </summary>
    private static bool MatchesIdentity(string toolId, TriageToolSettings tool) =>
        toolId == TicketCreateTool.ToolId
            ? string.Equals(tool.Category, "ticket_create", StringComparison.Ordinal) &&
              string.Equals(tool.LogicalTargetId, TicketCreateTool.LogicalTargetId, StringComparison.Ordinal)
            : string.Equals(tool.Category, "ticket_update", StringComparison.Ordinal) &&
              string.Equals(tool.LogicalTargetId,
                  TicketUpdatePostReportActionWorkflow.UpdateLogicalTargetId,
                  StringComparison.Ordinal);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
