using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;

namespace IncidentCompass.UnitTests;

public sealed class TicketToolSurfaceTests
{
    [Fact]
    public void CreateToolSurface_RequiresBothTicketsRoleAndGrant()
    {
        var executor = CreateExecutor();

        Assert.Equal("ticket_search", Assert.Single(Surface(executor, Configuration(true, true))).Name);
        Assert.Empty(Surface(executor, Configuration(true, false)));
        Assert.Empty(Surface(executor, Configuration(false, false)));
    }

    [Fact]
    public void TicketWritesCannotBecomeModelCallableThroughRoleOrConfig()
    {
        var executor = CreateExecutor();
        var configuration = Configuration(true, true);
        var tools = configuration.Tools.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        tools[TicketCreateTool.ToolId] = new TriageToolSettings(
            "external_action", null, null, null, "ticket_create", TicketCreateTool.LogicalTargetId);
        tools[TicketUpdatePostReportActionWorkflow.UpdateToolId] = new TriageToolSettings(
            "external_action", null, null, null, "ticket_update",
            TicketUpdatePostReportActionWorkflow.UpdateLogicalTargetId);
        var roles = configuration.Roles.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        roles["tickets"] = roles["tickets"] with
        {
            Tools =
            [
                "ticket_search",
                TicketCreateTool.ToolId,
                TicketUpdatePostReportActionWorkflow.UpdateToolId
            ]
        };

        var surface = Surface(executor, configuration with { Tools = tools, Roles = roles });

        Assert.Equal("ticket_search", Assert.Single(surface).Name);
    }

    private static IReadOnlyList<IncidentCompass.Application.Core.ModelClients.AiToolDefinition> Surface(
        WorkerToolCallExecutor executor,
        TriageConfiguration configuration) =>
        configuration.Roles.TryGetValue("tickets", out var role)
            ? executor.CreateToolSurface(configuration, role)
            : [];

    private static WorkerToolCallExecutor CreateExecutor() => new(
        [new TicketSearchTool(new UnavailableTicketSearchDouble())],
        new ToolRuleEngine(new ThrowingLedgerReader()),
        new TriageLedgerAppender(new ThrowingLedgerWriter()),
        new ThrowingCommitter(),
        TimeProvider.System);

    private static TriageConfiguration Configuration(bool withRole, bool withGrant)
    {
        var configuration = TestTriageConfiguration.Create();
        var roles = configuration.Roles.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (withRole)
        {
            roles["tickets"] = new TriageRoleSettings(
                "analysis-chat", "tickets", withGrant ? ["ticket_search"] : [], "{}");
        }

        var tools = configuration.Tools.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        tools["ticket_search"] = new TriageToolSettings("internal", null, null, null);
        return configuration with { Roles = roles, Tools = tools };
    }

    private sealed class UnavailableTicketSearchDouble : ITicketSearch
    {
        public Task<TicketSearchResult> SearchAsync(TicketSearchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(TicketSearchResult.Unavailable("ticket_search_unavailable"));
    }

    private sealed class ThrowingCommitter : ITriageToolResultCommitter
    {
        public Task<TriageArtifact> CommitSucceededAsync(TriageToolResultCommitRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingLedgerWriter : ITriageLedgerWriter
    {
        public Task<TriageLedgerEntry> AppendAsync(TriageLedgerAppendRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TriageLedgerEntry>> AppendBatchAsync(
            IReadOnlyList<TriageLedgerAppendRequest> requests,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingLedgerReader : ITriageLedgerReader
    {
        public Task<TriageBudgetLedgerUsage> ReadBudgetUsageAsync(TriageJob job, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> CountPolicyDecisionsAsync(
            TriageJob job, string toolName, string scope, TriageLedgerDecision decision, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> HasSuccessfulToolResultAsync(
            TriageJob job, string toolName, string scope, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TriageLedgerEntry>> ReadByFaultIdAsync(
            Guid faultId, string tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
