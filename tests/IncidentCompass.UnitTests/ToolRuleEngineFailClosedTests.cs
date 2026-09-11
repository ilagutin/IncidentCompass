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

    /// <summary>
    /// A scope the backend does not evaluate denies on both paths, and neither fact reader is asked.
    /// </summary>
    /// <remarks>
    /// This is the one rule field that used to be interpreted twice. The scope reached each path's
    /// fact reader as the configured string, and the two readers disagreed about what an
    /// unrecognized one meant: the triage-ledger reader narrowed it to the current attempt, the
    /// post-report action reader widened it to the whole job. Neither refused it, so the same rule
    /// could allow on one of the two paths the engine exists to unify and deny on the other. The
    /// scope is now parsed once, before any reader is reached, which is why this asserts that no
    /// fact was read at all: a reader that is never called cannot guess.
    /// </remarks>
    [Theory]
    [InlineData("fault")]
    [InlineData("tenant")]
    [InlineData("Attempt")]
    [InlineData("")]
    public async Task UnrecognizedRuleScopeDeniesBothDecisionPathsWithoutAskingEitherFactReader(string scope)
    {
        var rule = new TriageRuleSettings(TriageRuleTypes.RateCap, "*", scope, 2, null);
        var reader = new FactReader();

        var (immediate, external) = await DecideBothPathsWithoutThrowingAsync(rule, reader);

        AssertDenied(immediate, ToolPolicyDenialReasons.UnknownRuleScope);
        AssertDenied(external, ToolPolicyDenialReasons.UnknownRuleScope);
        Assert.Equal(0, reader.FactReads);
    }

    /// <summary>
    /// The two spellings the configuration loader admits still reach the readers as themselves, so
    /// the refusal above is about the scope being unrecognized and not about scopes in general.
    /// </summary>
    [Theory]
    [InlineData(ToolRuleScopes.AttemptName, ToolRuleScope.Attempt)]
    [InlineData(ToolRuleScopes.JobName, ToolRuleScope.Job)]
    public async Task AConfiguredScopeReachesBothFactReadersAsTheSameParsedWindow(
        string configuredScope,
        ToolRuleScope expected)
    {
        var rule = new TriageRuleSettings(TriageRuleTypes.RateCap, "*", configuredScope, 2, null);
        var reader = new FactReader();

        var (immediate, external) = await DecideBothPathsWithoutThrowingAsync(rule, reader);

        Assert.Equal(TriageLedgerDecision.Allowed, immediate.Decision);
        Assert.Equal(TriageLedgerDecision.Allowed, external.Decision);
        Assert.Equal([expected, expected], reader.Scopes);
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
        DecideBothPathsWithoutThrowingAsync(TriageRuleSettings rule, FactReader? factReader = null)
    {
        var reader = factReader ?? new FactReader();
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

    /// <summary>
    /// Stands in for both the immediate path's ledger reader and the post-report path's fact reader,
    /// recording every scope either one is handed. It is the one class here that holds state, which
    /// is what lets a test say "nothing was read" rather than only "the answer was a denial".
    /// </summary>
    private sealed class FactReader : ITriageLedgerReader, IToolRuleFactReader
    {
        public List<ToolRuleScope> Scopes { get; } = [];

        public int FactReads => Scopes.Count;

        public Task<int> CountPolicyDecisionsAsync(
            TriageJob job, string toolName, ToolRuleScope scope, TriageLedgerDecision decision,
            CancellationToken cancellationToken)
        {
            Scopes.Add(scope);
            return Task.FromResult(0);
        }

        public Task<bool> HasSuccessfulToolResultAsync(
            TriageJob job, string toolName, ToolRuleScope scope, CancellationToken cancellationToken)
        {
            Scopes.Add(scope);
            return Task.FromResult(true);
        }

        public Task<int> CountAcceptedUsesAsync(
            string toolName, ToolRuleScope scope, CancellationToken cancellationToken)
        {
            Scopes.Add(scope);
            return Task.FromResult(0);
        }

        public Task<bool> HasSuccessfulToolResultAsync(
            string toolName, ToolRuleScope scope, CancellationToken cancellationToken)
        {
            Scopes.Add(scope);
            return Task.FromResult(true);
        }

        public Task<TriageBudgetLedgerUsage> ReadBudgetUsageAsync(
            TriageJob job, CancellationToken cancellationToken) =>
            Task.FromResult(new TriageBudgetLedgerUsage(0, 0));

        public Task<IReadOnlyList<FaultLedgerEntry>> ReadByFaultIdAsync(
            Guid faultId, string tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FaultLedgerEntry>>([]);
    }
}
