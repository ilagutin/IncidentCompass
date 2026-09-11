using System.Text.Json;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Application.SourceContext;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Domain.Incidents.Statuses;
using IncidentCompass.Infrastructure.Remediation;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Proves, rather than asserts, that no investigation or remediation model can name a branch push.
/// </summary>
/// <remarks>
/// Three independent facts have to hold, and each is checked against production code rather than
/// against a comment. The tool surface a role produces is built only from
/// <see cref="IImmediateAgentTool" /> implementations; the push adapter is not one; and the shipped
/// configuration neither grants the id to a role nor allows the action. Any one of the three failing
/// is a defect, so all three are here.
/// </remarks>
public sealed class BranchPushToolSurfaceTests
{
    /// <summary>
    /// The type-level fact. A model turn can only reach a tool the executor can execute, and the
    /// executor only accepts immediate tools.
    /// </summary>
    [Fact]
    public void ThePushAdapterIsAnExternalActionAndNotAModelCallableTool()
    {
        Assert.True(typeof(IExternalActionTool).IsAssignableFrom(typeof(BranchPushActionTool)));
        Assert.False(typeof(IImmediateAgentTool).IsAssignableFrom(typeof(BranchPushActionTool)));
        Assert.Equal(AgentToolCapability.ExternalAction, BranchPushToolDescriptor.Descriptor.Capability);
        Assert.Equal(ActionCategory.BranchPush, BranchPushToolDescriptor.Descriptor.Category);
    }

    /// <summary>
    /// The configuration-level fact. Declaring the tool and granting it to a role - the strongest
    /// thing configuration can say - still produces a surface without it.
    /// </summary>
    [Fact]
    public void GrantingThePushToARoleDoesNotPutItOnAModelSurface()
    {
        var executor = CreateExecutor();
        var configuration = ConfigurationGrantingEverythingTo("source");

        var surface = executor.CreateToolSurface(configuration, configuration.Roles["source"]);

        Assert.Equal("source_lookup", Assert.Single(surface).Name);
    }

    /// <summary>
    /// The shipped-configuration fact. The capability exists in the file so that a configuration
    /// naming it validates, and it is off: no role is granted it and no action allows it.
    /// </summary>
    [Fact]
    public void TheShippedConfigurationDeclaresThePushAndEnablesNothing()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TriageConfigurationFileLocator.Shipped()));
        var root = document.RootElement;
        var tool = root.GetProperty("Tools").GetProperty(BranchPushToolDescriptor.ToolId);

        Assert.Equal("external_action", tool.GetProperty("Kind").GetString());
        Assert.Equal("branch_push", tool.GetProperty("Category").GetString());
        Assert.Equal(
            BranchPushToolDescriptor.LogicalTargetId, tool.GetProperty("LogicalTargetId").GetString());
        Assert.Equal("disabled", tool.GetProperty("Mode").GetString());
        Assert.Empty(root.GetProperty("Actions").GetProperty("AllowedTools").EnumerateArray());
        Assert.All(
            root.GetProperty("Roles").EnumerateObject(),
            role => Assert.DoesNotContain(
                role.Value.GetProperty("Tools").EnumerateArray(),
                granted => granted.GetString() == BranchPushToolDescriptor.ToolId));
        Assert.DoesNotContain(
            root.GetProperty("Orchestrator").GetProperty("Tools").EnumerateArray(),
            granted => granted.GetString() == BranchPushToolDescriptor.ToolId);
    }

    private static WorkerToolCallExecutor CreateExecutor() => new(
        [new SourceLookupTool(new UnavailableLookup())],
        new ToolRuleEngine(new ThrowingLedgerReader()),
        new TriageLedgerAppender(new ThrowingLedgerWriter()),
        new ThrowingCommitter(),
        TimeProvider.System);

    private static TriageConfiguration ConfigurationGrantingEverythingTo(string roleId)
    {
        var configuration = TestTriageConfiguration.Create();
        var tools = configuration.Tools.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        tools["source_lookup"] = new TriageToolSettings("internal", null, null, null);
        tools[BranchPushToolDescriptor.ToolId] = new TriageToolSettings(
            "external_action", null, null, null, "branch_push", BranchPushToolDescriptor.LogicalTargetId);
        var roles = configuration.Roles.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        roles[roleId] = new TriageRoleSettings(
            "analysis-chat", "source", ["source_lookup", BranchPushToolDescriptor.ToolId], "{}");
        return configuration with { Roles = roles, Tools = tools };
    }

    private sealed class UnavailableLookup : ISourceContextLookup
    {
        public Task<SourceLookupResult> LookupAsync(
            SourceLookupRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(SourceLookupResult.Unavailable("source_lookup_unavailable"));
    }

    private sealed class ThrowingCommitter : ITriageToolResultCommitter
    {
        public Task<TriageArtifact> CommitSucceededAsync(
            TriageToolResultCommitRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingLedgerWriter : ITriageLedgerWriter
    {
        public Task<TriageLedgerEntry> AppendAsync(
            TriageLedgerAppendRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TriageLedgerEntry>> AppendBatchAsync(
            IReadOnlyList<TriageLedgerAppendRequest> requests,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingLedgerReader : ITriageLedgerReader
    {
        public Task<TriageBudgetLedgerUsage> ReadBudgetUsageAsync(
            TriageJob job,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> CountPolicyDecisionsAsync(
            TriageJob job,
            string toolName,
            ToolRuleScope scope,
            TriageLedgerDecision decision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> HasSuccessfulToolResultAsync(
            TriageJob job,
            string toolName,
            ToolRuleScope scope,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<FaultLedgerEntry>> ReadByFaultIdAsync(
            Guid faultId,
            string tenantId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
