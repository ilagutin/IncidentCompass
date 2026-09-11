using System.Text.Json;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Infrastructure.Remediation;
using IncidentCompass.Infrastructure.Tickets;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Proves, rather than asserts, that no model can name the last two links of the publication chain and
/// that the shipped configuration leaves both switched off.
/// </summary>
public sealed class PublicationToolSurfaceTests
{
    [Fact]
    public void TheAdaptersAreExternalActionsAndNotModelCallableTools()
    {
        Assert.True(typeof(IExternalActionTool).IsAssignableFrom(typeof(PullRequestActionTool)));
        Assert.False(typeof(IImmediateAgentTool).IsAssignableFrom(typeof(PullRequestActionTool)));
        Assert.True(typeof(IExternalActionTool).IsAssignableFrom(
            typeof(GitHubIssueBacklinkExternalActionTool)));
        Assert.False(typeof(IImmediateAgentTool).IsAssignableFrom(
            typeof(GitHubIssueBacklinkExternalActionTool)));
        Assert.Equal(
            AgentToolCapability.ExternalAction, PullRequestToolDescriptor.Descriptor.Capability);
        Assert.Equal(ActionCategory.PrCreate, PullRequestToolDescriptor.Descriptor.Category);
        Assert.Equal(
            AgentToolCapability.ExternalAction, TicketBacklinkDescriptor.Descriptor.Capability);
        Assert.Equal(ActionCategory.TicketUpdate, TicketBacklinkDescriptor.Descriptor.Category);
    }

    /// <summary>
    /// Each link is its own switch. An operator who wants branches published but no pull requests
    /// opened, or pull requests opened but no ticket comments written, turns one off and the chain
    /// stops there, which only works because the ids are distinct.
    /// </summary>
    [Fact]
    public void EveryLinkInTheChainHasItsOwnToolIdAndIdempotencyKey()
    {
        Assert.Equal(
            4,
            new HashSet<string>(
                [
                    RemediationApplyToolDescriptor.ToolId,
                    BranchPushToolDescriptor.ToolId,
                    PullRequestToolDescriptor.ToolId,
                    TicketBacklinkDescriptor.ToolId
                ],
                StringComparer.Ordinal).Count);
        Assert.NotEqual(
            TicketBacklinkDescriptor.ToolId,
            Application.Governance.PostReportActions.TicketUpdatePostReportActionWorkflow.UpdateToolId);
    }

    [Fact]
    public void TheShippedConfigurationDeclaresBothAndEnablesNothing()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TriageConfigurationFileLocator.Shipped()));
        var root = document.RootElement;

        AssertDeclaredAndDisabled(
            root, PullRequestToolDescriptor.ToolId, "pr_create", PullRequestToolDescriptor.LogicalTargetId);
        AssertDeclaredAndDisabled(
            root, TicketBacklinkDescriptor.ToolId, "ticket_update", TicketBacklinkDescriptor.LogicalTargetId);
        Assert.Empty(root.GetProperty("Actions").GetProperty("AllowedTools").EnumerateArray());
    }

    private static void AssertDeclaredAndDisabled(
        JsonElement root,
        string toolId,
        string category,
        string logicalTargetId)
    {
        var tool = root.GetProperty("Tools").GetProperty(toolId);
        Assert.Equal("external_action", tool.GetProperty("Kind").GetString());
        Assert.Equal(category, tool.GetProperty("Category").GetString());
        Assert.Equal(logicalTargetId, tool.GetProperty("LogicalTargetId").GetString());
        Assert.Equal("disabled", tool.GetProperty("Mode").GetString());
        Assert.All(
            root.GetProperty("Roles").EnumerateObject(),
            role => Assert.DoesNotContain(
                role.Value.GetProperty("Tools").EnumerateArray(),
                granted => granted.GetString() == toolId));
        Assert.DoesNotContain(
            root.GetProperty("Orchestrator").GetProperty("Tools").EnumerateArray(),
            granted => granted.GetString() == toolId);
    }
}
