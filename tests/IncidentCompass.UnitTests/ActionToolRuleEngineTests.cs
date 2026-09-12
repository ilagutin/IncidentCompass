using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Domain.Incidents.Statuses;

namespace IncidentCompass.UnitTests;

public sealed class ActionToolRuleEngineTests
{
    private static readonly TriageJob Job = new(
        Guid.NewGuid(), Guid.NewGuid(), TriageJobStatus.Succeeded, 1,
        null, null, null, null, null, "config-hash", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task NotificationCanAutoApproveButEveryWriteRequiresApproval()
    {
        var facts = new LedgerReader();
        var engine = new ToolRuleEngine(facts);
        var notification = await engine.DecideExternalAsync(
            Configuration(ActionCategory.Notification), Descriptor(ActionCategory.Notification), facts,
            TestContext.Current.CancellationToken);
        var write = await engine.DecideExternalAsync(
            Configuration(ActionCategory.TicketCreate), Descriptor(ActionCategory.TicketCreate), facts,
            TestContext.Current.CancellationToken);

        Assert.Equal(TriageLedgerDecision.Allowed, notification.Decision);
        Assert.Equal(TriageLedgerDecision.ApprovalRequired, write.Decision);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task GlobalForceAndExactRuleRequireNotificationApproval(bool force, bool exactRule)
    {
        var facts = new LedgerReader();
        var result = await new ToolRuleEngine(facts).DecideExternalAsync(
            Configuration(ActionCategory.Notification, forceApproval: force, exactApprovalRule: exactRule),
            Descriptor(ActionCategory.Notification),
            facts,
            TestContext.Current.CancellationToken);

        Assert.Equal(TriageLedgerDecision.ApprovalRequired, result.Decision);
    }

    [Theory]
    [InlineData("live", "dry_run", ActionExecutionMode.DryRun)]
    [InlineData("dry_run", "live", ActionExecutionMode.DryRun)]
    [InlineData("live", null, ActionExecutionMode.Live)]
    public async Task GlobalModeIsHardCeilingAndToolOverrideOnlyTightens(
        string globalMode,
        string? toolMode,
        ActionExecutionMode expected)
    {
        var facts = new LedgerReader();
        var result = await new ToolRuleEngine(facts).DecideExternalAsync(
            Configuration(ActionCategory.Notification, globalMode: globalMode, toolMode: toolMode),
            Descriptor(ActionCategory.Notification),
            facts,
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.EffectiveMode);
    }

    /// <summary>
    /// Every ordered pair of execution modes, so the ladder is pinned rather than inferred from the
    /// four pairs the engine-level tests happen to reach. Reordering an arm so that a globally
    /// dry-run tool overridden to disabled resolved to <c>DryRun</c> would let a denied action
    /// execute, which is a governance change no other test observes.
    /// </summary>
    [Theory]
    [InlineData(ActionExecutionMode.Live, ActionExecutionMode.Live, ActionExecutionMode.Live)]
    [InlineData(ActionExecutionMode.Live, ActionExecutionMode.DryRun, ActionExecutionMode.DryRun)]
    [InlineData(ActionExecutionMode.Live, ActionExecutionMode.Disabled, ActionExecutionMode.Disabled)]
    [InlineData(ActionExecutionMode.DryRun, ActionExecutionMode.Live, ActionExecutionMode.DryRun)]
    [InlineData(ActionExecutionMode.DryRun, ActionExecutionMode.DryRun, ActionExecutionMode.DryRun)]
    [InlineData(ActionExecutionMode.DryRun, ActionExecutionMode.Disabled, ActionExecutionMode.Disabled)]
    [InlineData(ActionExecutionMode.Disabled, ActionExecutionMode.Live, ActionExecutionMode.Disabled)]
    [InlineData(ActionExecutionMode.Disabled, ActionExecutionMode.DryRun, ActionExecutionMode.Disabled)]
    [InlineData(ActionExecutionMode.Disabled, ActionExecutionMode.Disabled, ActionExecutionMode.Disabled)]
    public void MostRestrictiveModeHoldsForEveryModePair(
        ActionExecutionMode first,
        ActionExecutionMode second,
        ActionExecutionMode expected)
    {
        Assert.Equal(expected, ActionGovernanceDefaults.MostRestrictive(first, second));
    }

    [Fact]
    public async Task DisabledUnlistedAliasAndRegistrationMismatchFailClosed()
    {
        var facts = new LedgerReader();
        var engine = new ToolRuleEngine(facts);
        var disabled = await engine.DecideExternalAsync(
            Configuration(ActionCategory.Notification, globalMode: "disabled"),
            Descriptor(ActionCategory.Notification), facts, TestContext.Current.CancellationToken);
        var unlisted = await engine.DecideExternalAsync(
            Configuration(ActionCategory.Notification, allowed: false),
            Descriptor(ActionCategory.Notification), facts, TestContext.Current.CancellationToken);
        var mismatch = await engine.DecideExternalAsync(
            Configuration(ActionCategory.Notification),
            Descriptor(ActionCategory.TicketUpdate), facts, TestContext.Current.CancellationToken);

        Assert.Equal("action_disabled", disabled.Reason);
        Assert.Equal("action_not_granted", unlisted.Reason);
        Assert.Equal("action_registration_mismatch", mismatch.Reason);
    }

    [Fact]
    public async Task ImmediateReadAndExternalActionUseSameEngineWithCapabilitySpecificFacts()
    {
        var reader = new LedgerReader();
        var engine = new ToolRuleEngine(reader);
        var immediate = await engine.DecideImmediateAsync(
            Job, TestTriageConfiguration.Create(), "memory", "memory_search",
            TestContext.Current.CancellationToken);
        var action = await engine.DecideExternalAsync(
            Configuration(ActionCategory.Notification), Descriptor(ActionCategory.Notification), reader,
            TestContext.Current.CancellationToken);

        Assert.Equal(TriageLedgerDecision.Allowed, immediate.Decision);
        Assert.Equal(1, reader.PolicyDecisionCountCalls);
        Assert.Equal(1, reader.AcceptedUseCountCalls);
        Assert.Equal(TriageLedgerDecision.Allowed, action.Decision);
    }

    [Fact]
    public void InvestigationSurfaceCannotAdvertiseExternalActionFromRoleOrConfigInput()
    {
        var configuration = Configuration(ActionCategory.Notification) with
        {
            Roles = new Dictionary<string, TriageRoleSettings>(StringComparer.Ordinal)
            {
                ["analysis"] = new("analysis-chat", "analysis", ["action_test"], "{}")
            }
        };
        var executor = new WorkerToolCallExecutor(
            [], new ToolRuleEngine(new LedgerReader()),
            new TriageLedgerAppender(new LedgerWriter()), new ToolResultCommitter(), TimeProvider.System);

        Assert.Empty(executor.CreateToolSurface(configuration, configuration.Roles["analysis"]));
    }

    [Fact]
    public void RegistryRequiresUniqueCaseSensitiveBackendIdentity()
    {
        var descriptor = Descriptor(ActionCategory.Notification);

        Assert.Throws<InvalidOperationException>(() => new AgentToolRegistry([descriptor, descriptor]));

        var registry = new AgentToolRegistry([descriptor]);
        Assert.True(registry.TryGet("action_test", out var exact));
        Assert.Equal(descriptor, exact);
        Assert.False(registry.TryGet("ACTION_TEST", out _));
        Assert.Throws<InvalidOperationException>(() => new AgentToolRegistry([
            descriptor with { ToolId = "action:test" }
        ]));
    }

    private static TriageConfiguration Configuration(
        ActionCategory category,
        bool forceApproval = false,
        bool exactApprovalRule = false,
        string globalMode = "live",
        string? toolMode = null,
        bool allowed = true)
    {
        var configuration = TestTriageConfiguration.Create();
        return configuration with
        {
            Tools = new Dictionary<string, TriageToolSettings>(StringComparer.Ordinal)
            {
                ["action_test"] = new(
                    "external_action", null, null, null,
                    category.ToStorageValue(), "test:target", toolMode)
            },
            Rules = exactApprovalRule
                ? [new TriageRuleSettings("requires_approval", "action_test", "attempt", null, null),
                    new TriageRuleSettings("rate_cap", "action_test", "attempt", 2, null)]
                : [new TriageRuleSettings("rate_cap", "action_test", "attempt", 2, null)],
            Actions = new TriageActionSettings(
                allowed ? ["action_test"] : [], globalMode, forceApproval, 60)
        };
    }

    private static AgentToolDescriptor Descriptor(ActionCategory category) =>
        new("action_test", AgentToolCapability.ExternalAction, category, "test:target");

    private sealed class LedgerReader : ITriageLedgerReader, IToolRuleFactReader
    {
        public int PolicyDecisionCountCalls { get; private set; }
        public int AcceptedUseCountCalls { get; private set; }

        public Task<int> CountPolicyDecisionsAsync(
            TriageJob job, string toolName, ToolRuleScope scope, TriageLedgerDecision decision,
            CancellationToken cancellationToken)
        {
            PolicyDecisionCountCalls++;
            return Task.FromResult(0);
        }

        public Task<bool> HasSuccessfulToolResultAsync(
            TriageJob job, string toolName, ToolRuleScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<int> CountAcceptedUsesAsync(
            string toolName, ToolRuleScope scope, CancellationToken cancellationToken)
        {
            AcceptedUseCountCalls++;
            return Task.FromResult(0);
        }

        public Task<bool> HasSuccessfulToolResultAsync(
            string toolName, ToolRuleScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<TriageBudgetLedgerUsage> ReadBudgetUsageAsync(
            TriageJob job, CancellationToken cancellationToken) =>
            Task.FromResult(new TriageBudgetLedgerUsage(0, 0));

        public Task<IReadOnlyList<FaultLedgerEntry>> ReadByFaultIdAsync(
            Guid faultId, string tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FaultLedgerEntry>>([]);
    }

    private sealed class LedgerWriter : ITriageLedgerWriter
    {
        public Task<TriageLedgerEntry> AppendAsync(
            TriageLedgerAppendRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TriageLedgerEntry>> AppendBatchAsync(
            IReadOnlyList<TriageLedgerAppendRequest> requests,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ToolResultCommitter : ITriageToolResultCommitter
    {
        public Task<TriageArtifact> CommitSucceededAsync(
            TriageToolResultCommitRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
