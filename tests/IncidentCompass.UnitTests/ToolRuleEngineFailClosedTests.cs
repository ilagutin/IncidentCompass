using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Domain.Incidents.Statuses;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The rule engine is the single tool-policy decision path for immediate worker reads and for
/// backend-owned post-report action proposals. A rule it cannot evaluate must deny on both paths
/// rather than be skipped, and it must never throw out of policy evaluation.
/// </summary>
public sealed class ToolRuleEngineFailClosedTests
{
    private static readonly TriageJob Job = new(
        Guid.NewGuid(), Guid.NewGuid(), TriageJobStatus.Succeeded, 1,
        null, null, null, null, null, "config-hash", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task UnknownRuleTypeDeniesBothDecisionPathsInsteadOfFallingThroughToAllowed()
    {
        var rule = new TriageRuleSettings("escalate_to_human", "*", "attempt", null, null);

        var (immediate, external) = await DecideBothPathsWithoutThrowingAsync(rule);

        AssertDenied(immediate, ToolPolicyDenialReasons.UnknownRuleType);
        AssertDenied(external, ToolPolicyDenialReasons.UnknownRuleType);
        Assert.Contains("escalate_to_human", immediate.Reason, StringComparison.Ordinal);
        Assert.Contains("memory_search", immediate.Reason, StringComparison.Ordinal);
        Assert.Contains("escalate_to_human", external.Reason, StringComparison.Ordinal);
        Assert.Contains("action_test", external.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PreconditionWithoutPrerequisiteDeniesBothDecisionPathsAndDoesNotThrow(string? prerequisite)
    {
        var rule = new TriageRuleSettings(
            TriageRuleTypes.Precondition, "*", "attempt", null, prerequisite);

        var (immediate, external) = await DecideBothPathsWithoutThrowingAsync(rule);

        AssertDenied(immediate, ToolPolicyDenialReasons.PreconditionMissingPrerequisite);
        AssertDenied(external, ToolPolicyDenialReasons.PreconditionMissingPrerequisite);
    }

    [Fact]
    public async Task RateCapWithoutMaxDeniesBothDecisionPathsInsteadOfDegradingToACapOfZero()
    {
        var rule = new TriageRuleSettings(TriageRuleTypes.RateCap, "*", "attempt", null, null);

        var (immediate, external) = await DecideBothPathsWithoutThrowingAsync(rule);

        AssertDenied(immediate, ToolPolicyDenialReasons.RateCapMissingMax);
        AssertDenied(external, ToolPolicyDenialReasons.RateCapMissingMax);
        Assert.NotEqual(ToolPolicyDenialReasons.RateCapExceeded, immediate.ReasonCode);
        Assert.NotEqual(ToolPolicyDenialReasons.RateCapExceeded, external.ReasonCode);
    }

    [Fact]
    public async Task WellFormedRulesStillDecideNormallyOnBothPaths()
    {
        var rule = new TriageRuleSettings(TriageRuleTypes.RateCap, "*", "attempt", 2, null);

        var (immediate, external) = await DecideBothPathsWithoutThrowingAsync(rule);

        Assert.Equal(TriageLedgerDecision.Allowed, immediate.Decision);
        Assert.Equal(TriageLedgerDecision.Allowed, external.Decision);
    }

    /// <summary>
    /// A denial's cause is its reason code, and the stored reason always leads with that code, so both
    /// are asserted: the code is what a read model aggregates on and the reason is what an operator
    /// reads out of the ledger.
    /// </summary>
    private static void AssertDenied(ToolRulePolicyResult result, string expectedReasonCode)
    {
        Assert.Equal(TriageLedgerDecision.Denied, result.Decision);
        Assert.False(result.MayProceed);
        Assert.Equal(expectedReasonCode, result.ReasonCode);
        Assert.StartsWith(expectedReasonCode, result.Reason, StringComparison.Ordinal);
    }

    private static async Task<(ToolRulePolicyResult Immediate, ToolRulePolicyResult External)>
        DecideBothPathsWithoutThrowingAsync(TriageRuleSettings rule)
    {
        var reader = new FactReader();
        var engine = new ToolRuleEngine(reader);

        ToolRulePolicyResult? immediate = null;
        var immediateFailure = await Record.ExceptionAsync(async () =>
            immediate = await engine.DecideImmediateAsync(
                Job,
                ImmediateConfiguration(rule),
                "memory",
                "memory_search",
                TestContext.Current.CancellationToken));

        ToolRulePolicyResult? external = null;
        var externalFailure = await Record.ExceptionAsync(async () =>
            external = await engine.DecideExternalAsync(
                ExternalActionConfiguration(rule),
                new AgentToolDescriptor(
                    "action_test", AgentToolCapability.ExternalAction, ActionCategory.Notification, "test:target"),
                reader,
                TestContext.Current.CancellationToken));

        Assert.Null(immediateFailure);
        Assert.Null(externalFailure);
        Assert.NotNull(immediate);
        Assert.NotNull(external);
        return (immediate, external);
    }

    private static TriageConfiguration ImmediateConfiguration(TriageRuleSettings rule) =>
        TestTriageConfiguration.Create() with { Rules = [rule] };

    private static TriageConfiguration ExternalActionConfiguration(TriageRuleSettings rule) =>
        TestTriageConfiguration.Create() with
        {
            Tools = new Dictionary<string, TriageToolSettings>(StringComparer.Ordinal)
            {
                ["action_test"] = new(
                    "external_action", null, null, null,
                    ActionCategory.Notification.ToStorageValue(), "test:target", null)
            },
            Rules = [rule],
            Actions = new TriageActionSettings(["action_test"], "live", false, 60)
        };

    private sealed class FactReader : ITriageLedgerReader, IToolRuleFactReader
    {
        public Task<int> CountPolicyDecisionsAsync(
            TriageJob job, string toolName, string scope, TriageLedgerDecision decision,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<bool> HasSuccessfulToolResultAsync(
            TriageJob job, string toolName, string scope, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<int> CountAcceptedUsesAsync(
            string toolName, string scope, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<bool> HasSuccessfulToolResultAsync(
            string toolName, string scope, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<TriageBudgetLedgerUsage> ReadBudgetUsageAsync(
            TriageJob job, CancellationToken cancellationToken) =>
            Task.FromResult(new TriageBudgetLedgerUsage(0, 0));

        public Task<IReadOnlyList<TriageLedgerEntry>> ReadByFaultIdAsync(
            Guid faultId, string tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TriageLedgerEntry>>([]);
    }
}
