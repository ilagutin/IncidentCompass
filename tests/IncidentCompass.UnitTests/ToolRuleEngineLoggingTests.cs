using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;
using Microsoft.Extensions.Logging;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The single tool-policy decision path must make its allow, deny and approval-required outcomes
/// visible in application logs with stable event ids and bounded reason tokens.
/// </summary>
public sealed class ToolRuleEngineLoggingTests
{
    private static readonly TriageJob Job = new(
        Guid.NewGuid(), Guid.NewGuid(), TriageJobStatus.Processing, 2,
        null, null, null, null, null, "config-hash", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task DecideImmediateAsync_DenialLogsTheWarningEventWithItsReasonToken()
    {
        var logger = new RecordingLogger<ToolRuleEngine>();
        var engine = new ToolRuleEngine(new FactReader(), logger);
        var configuration = TestTriageConfiguration.Create() with
        {
            Rules = [new TriageRuleSettings("escalate_to_human", "*", "attempt", null, null)]
        };

        var result = await engine.DecideImmediateAsync(
            Job, configuration, "memory", "memory_search", TestContext.Current.CancellationToken);

        Assert.Equal(TriageLedgerDecision.Denied, result.Decision);

        var denial = logger.Single(3502);
        Assert.Equal(LogLevel.Warning, denial.Level);
        Assert.Contains("unknown_rule_type", denial.Message, StringComparison.Ordinal);
        Assert.Contains("memory_search", denial.Message, StringComparison.Ordinal);
        Assert.Contains("memory", denial.Message, StringComparison.Ordinal);
        Assert.Equal(-1, logger.IndexOf(3501));
        Assert.Equal(-1, logger.IndexOf(3503));
    }

    [Fact]
    public async Task DecideImmediateAsync_RoutineAllowStaysBelowWarning()
    {
        var logger = new RecordingLogger<ToolRuleEngine>();
        var engine = new ToolRuleEngine(new FactReader(), logger);

        var result = await engine.DecideImmediateAsync(
            Job,
            TestTriageConfiguration.Create(),
            "memory",
            "memory_search",
            TestContext.Current.CancellationToken);

        Assert.Equal(TriageLedgerDecision.Allowed, result.Decision);
        Assert.Equal(LogLevel.Debug, logger.Single(3501).Level);
        Assert.Equal(-1, logger.IndexOf(3502));
    }

    private sealed class FactReader : ITriageLedgerReader
    {
        public Task<int> CountPolicyDecisionsAsync(
            TriageJob job, string toolName, ToolRuleScope scope, TriageLedgerDecision decision,
            CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<bool> HasSuccessfulToolResultAsync(
            TriageJob job, string toolName, ToolRuleScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<TriageBudgetLedgerUsage> ReadBudgetUsageAsync(
            TriageJob job, CancellationToken cancellationToken) =>
            Task.FromResult(new TriageBudgetLedgerUsage(0, 0));

        public Task<IReadOnlyList<FaultLedgerEntry>> ReadByFaultIdAsync(
            Guid faultId, string tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FaultLedgerEntry>>([]);
    }
}
