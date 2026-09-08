using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.SourceContext;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;

namespace IncidentCompass.UnitTests;

public sealed class SourceToolSurfaceTests
{
    [Fact]
    public void CreateToolSurface_RequiresBothSourceRoleAndGrant()
    {
        var executor = CreateExecutor();
        var configured = Configuration(withRole: true, withGrant: true);
        var ungranted = Configuration(withRole: true, withGrant: false);
        var removed = Configuration(withRole: false, withGrant: false);

        Assert.Equal("source_lookup", Assert.Single(Surface(executor, configured)).Name);
        Assert.Empty(Surface(executor, ungranted));
        Assert.Empty(Surface(executor, removed));
    }

    private static IReadOnlyList<IncidentCompass.Application.Core.ModelClients.AiToolDefinition> Surface(
        WorkerToolCallExecutor executor,
        TriageConfiguration configuration)
    {
        return configuration.Roles.TryGetValue("source", out var role)
            ? executor.CreateToolSurface(configuration, role)
            : [];
    }

    private static WorkerToolCallExecutor CreateExecutor()
    {
        var tool = new SourceLookupTool(new UnavailableLookup(), TimeProvider.System);
        return new WorkerToolCallExecutor(
            [tool],
            new ToolRuleEngine(new ThrowingLedgerReader()),
            new TriageLedgerAppender(new ThrowingLedgerWriter()),
            new ThrowingCommitter());
    }

    private static TriageConfiguration Configuration(bool withRole, bool withGrant)
    {
        var baseConfiguration = TestTriageConfiguration.Create();
        var roles = baseConfiguration.Roles.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (withRole)
        {
            roles["source"] = new TriageRoleSettings(
                "analysis-chat", "source", withGrant ? ["source_lookup"] : [], "{}");
        }

        var tools = baseConfiguration.Tools.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        tools["source_lookup"] = new TriageToolSettings("internal", null, null, null);
        return baseConfiguration with { Roles = roles, Tools = tools };
    }

    private sealed class UnavailableLookup : ISourceContextLookup
    {
        public Task<SourceLookupResult> LookupAsync(SourceLookupRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(SourceLookupResult.Unavailable("source_lookup_unavailable"));
    }

    private sealed class ThrowingCommitter : ITriageToolResultCommitter
    {
        public Task<TriageArtifact> CommitSucceededAsync(
            TriageToolResultCommitRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ThrowingLedgerWriter : ITriageLedgerWriter
    {
        public Task<TriageLedgerEntry> AppendAsync(
            TriageLedgerAppendRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<TriageLedgerEntry>> AppendBatchAsync(
            IReadOnlyList<TriageLedgerAppendRequest> requests,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ThrowingLedgerReader : ITriageLedgerReader
    {
        public Task<TriageBudgetLedgerUsage> ReadBudgetUsageAsync(
            TriageJob job,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> CountPolicyDecisionsAsync(
            TriageJob job,
            string toolName,
            string scope,
            TriageLedgerDecision decision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> HasSuccessfulToolResultAsync(
            TriageJob job,
            string toolName,
            string scope,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<TriageLedgerEntry>> ReadByFaultIdAsync(
            Guid faultId,
            string tenantId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
