using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.ActionApprovals.Testing;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Domain.Incidents.Statuses;
using IncidentCompass.Infrastructure.Postgres;
using IncidentCompass.Infrastructure.Tickets;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Governance.ActionApprovals;

internal sealed class PostgresActionProposalRepository(
    PostgresDataSourceProvider dataSourceProvider,
    IActionApprovalTransactionFaultInjector faultInjector,
    TimeProvider timeProvider,
    ToolRuleEngine ruleEngine,
    IOptions<GitHubIssuesOptions> ticketOptions) : IActionProposalRepository
{
    private readonly PostgresActionProvenanceGrounder grounder = new(ticketOptions.Value.ConfiguredRepository);
    private readonly PostgresActionProposalPolicyStore policyStore = new(dataSourceProvider, timeProvider);
    public Task<ActionProposalOrigin?> FindSafeOriginAsync(
        string tenantId,
        Guid originReportId,
        CancellationToken cancellationToken) =>
        policyStore.FindSafeOriginAsync(tenantId, originReportId, cancellationToken);

    public Task<bool> RecordDenialAsync(
        string tenantId,
        Guid originReportId,
        string? auditedToolId,
        string reasonCode,
        CancellationToken cancellationToken) =>
        policyStore.RecordDenialAsync(
            tenantId, originReportId, auditedToolId, reasonCode, cancellationToken);

    public Task<ActionProposalResult> CreateAsync(
        PreparedActionProposal proposal,
        CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "create action approval proposal",
            () => CreateTransactionAsync(proposal, cancellationToken));

    public Task<ActionProposalResult> CreateGovernedAsync(
        GovernedActionProposal proposal,
        CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "create governed action approval proposal",
            () => CreateGovernedTransactionAsync(proposal, cancellationToken));

    private async Task<ActionProposalResult> CreateTransactionAsync(
        PreparedActionProposal proposal,
        CancellationToken cancellationToken)
    {
        var payloadSha256 = ActionProposalValidator.ValidateAndComputePayloadHash(proposal);
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var existing = await PostgresActionProposalReplay.FindAsync(connection, transaction, proposal, cancellationToken);
            if (existing is not null)
            {
                await PostgresActionProposalReplay.ValidateEvidenceAsync(connection, transaction, existing, proposal, cancellationToken);
                var replay = PostgresActionProposalReplay.Validate(existing, proposal);
                await transaction.CommitAsync(cancellationToken);
                return replay;
            }
            var origin = await grounder.GroundAsync(
                connection, transaction, GroundingInput(proposal), cancellationToken);
            existing = await PostgresActionProposalReplay.FindAsync(connection, transaction, proposal, cancellationToken);
            if (existing is not null)
            {
                await PostgresActionProposalReplay.ValidateEvidenceAsync(connection, transaction, existing, proposal, cancellationToken);
                var replay = PostgresActionProposalReplay.Validate(existing, proposal);
                await transaction.CommitAsync(cancellationToken);
                return replay;
            }
            var action = await PostgresActionProposalCommitter.InsertAsync(
                connection, transaction, proposal, origin, payloadSha256,
                faultInjector, timeProvider, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ActionProposalResult(action, false);
        }
        catch (ActionProposalPolicyDeniedException)
        {
            throw;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<ActionProposalResult> CreateGovernedTransactionAsync(
        GovernedActionProposal proposal,
        CancellationToken cancellationToken)
    {
        var payloadSha256 = ActionProposalValidator.ValidateAndComputePayloadHash(proposal);
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var existing = await PostgresActionProposalReplay.FindAsync(
                connection, transaction, proposal, cancellationToken);
            if (existing is not null)
            {
                await PostgresActionProposalReplay.ValidateEvidenceAsync(
                    connection, transaction, existing, proposal, cancellationToken);
                var replay = PostgresActionProposalReplay.Validate(existing, proposal);
                await transaction.CommitAsync(cancellationToken);
                return replay;
            }

            var origin = await grounder.GroundAsync(
                connection, transaction, GroundingInput(proposal), cancellationToken);
            existing = await PostgresActionProposalReplay.FindAsync(
                connection, transaction, proposal, cancellationToken);
            if (existing is not null)
            {
                await PostgresActionProposalReplay.ValidateEvidenceAsync(
                    connection, transaction, existing, proposal, cancellationToken);
                var replay = PostgresActionProposalReplay.Validate(existing, proposal);
                await transaction.CommitAsync(cancellationToken);
                return replay;
            }

            if (!string.Equals(origin.ConfigHash, proposal.Configuration.ConfigHash, StringComparison.Ordinal))
            {
                await DenyAsync("configuration_invalid");
            }

            if (proposal.RegisteredTool.Category == ActionCategory.TicketCreate &&
                proposal.RegisteredTool.ToolId == TicketCreateTool.ToolId)
            {
                var eligibilityDenial = await PostgresTicketActionHistory.ValidateCreateProposalAsync(
                    connection, transaction, proposal, origin,
                    ticketOptions.Value.ConfiguredRepository,
                    cancellationToken);
                if (eligibilityDenial is not null)
                {
                    await DenyAsync(eligibilityDenial);
                }
            }

            await PostgresTicketUpdateEvidenceResolver.EnforceProposalAsync(connection, transaction, proposal, origin, ticketOptions.Value.ConfiguredRepository, DenyAsync, cancellationToken);
            if (proposal.RegisteredTool.Category == ActionCategory.Notification)
            {
                var notificationDenial = await PostgresActionProposalReplay.ApplyNotificationGuardAsync(
                    connection, transaction, proposal, origin, cancellationToken);
                if (notificationDenial is not null)
                {
                    await DenyAsync(notificationDenial);
                }
            }
            var facts = new PostgresActionToolRuleFactReader(connection, transaction, origin);
            var policy = await ruleEngine.DecideExternalAsync(
                proposal.Configuration, proposal.RegisteredTool, facts, cancellationToken);
            if (!policy.MayProceed)
            {
                await DenyAsync(ActionProposalDenialReason.NormalizePolicy(policy.ReasonCode));
            }

            var prepared = new PreparedActionProposal(
                proposal.TenantId, proposal.OriginReportId, proposal.ToolId, proposal.ProposalKey,
                proposal.RegisteredTool.Category!.Value, policy.EffectiveMode!.Value,
                proposal.RegisteredTool.LogicalTargetId!, proposal.AdapterBindingFingerprint,
                proposal.CanonicalPayload, proposal.ReviewSummary, proposal.ApprovalTtlMinutes,
                proposal.EvidenceArtifactIds, policy.Decision == TriageLedgerDecision.Allowed,
                policy.Reason);
            ActionProposalValidator.ValidateAndComputePayloadHash(prepared);
            var action = await PostgresActionProposalCommitter.InsertAsync(
                connection, transaction, prepared, origin, payloadSha256,
                faultInjector, timeProvider, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ActionProposalResult(action, false);

            async Task DenyAsync(string reasonCode)
            {
                await PostgresActionDenialWriter.InsertAsync(
                    connection, transaction, origin, proposal.OriginReportId,
                    proposal.ToolId, reasonCode, timeProvider.GetUtcNow(), cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                throw new ActionProposalPolicyDeniedException(reasonCode);
            }
        }
        catch (ActionProposalPolicyDeniedException)
        {
            throw;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
    private static ActionProposalGroundingInput GroundingInput(PreparedActionProposal proposal) =>
        new(proposal.TenantId, proposal.OriginReportId, proposal.EvidenceArtifactIds);
    private static ActionProposalGroundingInput GroundingInput(GovernedActionProposal proposal) =>
        new(proposal.TenantId, proposal.OriginReportId, proposal.EvidenceArtifactIds);
}
