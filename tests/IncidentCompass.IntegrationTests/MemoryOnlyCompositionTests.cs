using IncidentCompass.Application;
using IncidentCompass.Application.Core.Dispatching;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.Security;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Intake.Artifacts;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Intake.FaultGrouping;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Application.Investigation.Reports.Get;
using IncidentCompass.Application.Investigation.Reports.List;
using IncidentCompass.Application.Memory;
using IncidentCompass.Application.Observability.CostRollup;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IncidentCompass.IntegrationTests;

public sealed class MemoryOnlyCompositionTests
{
    [Fact]
    public void Application_CanBuildWithoutChatModelServices()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication(configuration);
        services.AddSingleton<MemoryOnlyUserContext>();
        services.AddSingleton<IUserContext>(serviceProvider =>
            serviceProvider.GetRequiredService<MemoryOnlyUserContext>());
        services.AddSingleton<IBackgroundUserContext>(serviceProvider =>
            serviceProvider.GetRequiredService<MemoryOnlyUserContext>());
        // IngestSignalCommandValidator (intake) depends on ITriageConfigurationRepository,
        // another Infrastructure-provided port. Same reasoning as above: supply a trivial
        // in-memory stand-in instead of pulling in IncidentCompass.Infrastructure.
        services.AddSingleton<ITriageConfigurationRepository, InMemoryTriageConfigurationRepository>();
        // FaultGroupingCoordinator/GroundedFactsAssembler/GetFaultQueryHandler (intake)
        // depend on these repository ports, all Infrastructure-provided. Same reasoning as above:
        // supply trivial in-memory stand-ins instead of pulling in IncidentCompass.Infrastructure.
        services.AddSingleton<ISignalRepository, InMemorySignalRepository>();
        services.AddSingleton<IFaultRepository, InMemoryFaultRepository>();
        services.AddSingleton<IIntakeUnitOfWork, InMemoryIntakeUnitOfWork>();
        services.AddSingleton<ITriageJobRepository, InMemoryTriageJobRepository>();
        services.AddSingleton<ITriageArtifactRepository, InMemoryTriageArtifactRepository>();
        services.AddSingleton<IRecurrenceStateRepository, InMemoryRecurrenceStateRepository>();
        services.AddSingleton<ITriageJobRuntimeRepository, InMemoryTriageJobRuntimeRepository>();
        services.AddSingleton<IPriorReportSummaryProvider, InMemoryPriorReportSummaryProvider>();
        services.AddSingleton<ITriageReportReadRepository, InMemoryTriageReportReadRepository>();
        services.AddSingleton<ITriageReportListRepository, InMemoryTriageReportListRepository>();
        services.AddSingleton<ITriageLedgerReader, InMemoryTriageLedgerReader>();
        services.AddSingleton<IModelCostRollupRepository, InMemoryModelCostRollupRepository>();
        services.AddSingleton<IActionProposalRepository, InMemoryActionProposalRepository>();
        services.AddSingleton<IActionApprovalReviewRepository, InMemoryActionApprovalReviewRepository>();
        services.AddSingleton<IEmbeddingClient, InMemoryEmbeddingClient>();
        services.AddSingleton<IMemoryRepository, InMemoryMemoryRepository>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IApplicationDispatcher>());
        Assert.Empty(scope.ServiceProvider.GetServices<IAiModelClient>());
    }

    private sealed class MemoryOnlyUserContext : IBackgroundUserContext
    {
        public bool IsAuthenticated => true;

        public string? UserId => "system";

        public string? TenantId => null;

        public IReadOnlyCollection<string> Roles => ["system"];

        public IReadOnlyCollection<string> Groups => [];
    }

    private sealed class InMemoryTriageConfigurationRepository : ITriageConfigurationRepository
    {
        private static readonly TriageConfiguration Configuration = new(
            ConfigHash: "memory-only-test-hash",
            Providers: new Dictionary<string, TriageProviderSettings>(StringComparer.Ordinal)
            {
                ["local-oai"] = new("OpenAICompatible", "http://localhost:1234/v1", "LOCAL_OAI_KEY")
            },
            Routes: new Dictionary<string, TriageRouteSettings>(StringComparer.Ordinal)
            {
                ["analysis-chat"] = new("Chat", "local-oai", "local-model", 0.1, 2000, 8192),
                ["report-chat"] = new("Chat", "local-oai", "local-model", 0.2, 4000, 8192),
                ["memory-embed"] = new("Embedding", "local-oai", "mock-memory-embedding-v1", null, null, null)
            },
            Orchestrator: new OrchestratorSettings(
                "orchestrator instructions",
                "report-chat",
                ["delegate", "publish_report"],
                new OrchestratorBudgetSettings(6, 200000, 120)),
            Roles: new Dictionary<string, TriageRoleSettings>(StringComparer.Ordinal)
            {
                ["analysis"] = new("analysis-chat", "analysis instructions", [], "analysis schema")
            },
            Tools: new Dictionary<string, TriageToolSettings>(StringComparer.Ordinal),
            Rules: [],
            Ingestion: new IngestionSettings(DefaultTenant: "local", AllowedSources: ["tester"]),
            FaultGrouping: new FaultGroupingSettings(
                LookbackMinutes: 15,
                SilenceWindowMinutes: 30,
                FingerprintVersion: 1,
                MassIssue: new MassIssueSettings(MinNeighborCount: 5, MinFingerprintStrength: "strong")),
            Redaction: RedactionSettings.Default);

        public Task<TriageConfiguration> GetCurrentAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Configuration);

        public Task<TriageConfiguration> GetByHashAsync(string configHash, CancellationToken cancellationToken) =>
            Task.FromResult(Configuration);
    }

    private sealed class InMemoryIntakeUnitOfWork : IIntakeUnitOfWork
    {
        public Task<TResult> ExecuteAsync<TResult>(
            Func<CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken) => operation(cancellationToken);
    }
    private sealed class InMemorySignalRepository : ISignalRepository
    {
        public Task InsertAsync(Signal signal, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ExistingSignalDelivery?> FindDeliveryAsync(
            string tenantId,
            string source,
            string deliveryKey,
            CancellationToken cancellationToken) => Task.FromResult<ExistingSignalDelivery?>(null);
        public Task AttachToFaultAsync(Guid signalId, Guid faultId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> CountDistinctNeighborsAsync(
            string tenantId,
            string serviceName,
            string environment,
            string fingerprint,
            int fingerprintVersion,
            string groupingRuleId,
            int groupingRuleVersion,
            DateTimeOffset windowStartUtc,
            DateTimeOffset windowEndUtc,
            CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class InMemoryFaultRepository : IFaultRepository
    {
        public Task<Fault?> FindOpenFaultAsync(
            string tenantId,
            string serviceName,
            string environment,
            string fingerprint,
            int fingerprintVersion,
            string groupingRuleId,
            int groupingRuleVersion,
            CancellationToken cancellationToken) => Task.FromResult<Fault?>(null);

        public Task<Fault?> FindMostRecentClosedFaultAsync(
            string tenantId,
            string serviceName,
            string environment,
            string fingerprint,
            int fingerprintVersion,
            string groupingRuleId,
            int groupingRuleVersion,
            CancellationToken cancellationToken) => Task.FromResult<Fault?>(null);

        public Task<Fault?> TryInsertAsync(Fault fault, CancellationToken cancellationToken) => Task.FromResult<Fault?>(fault);

        public Task<Fault?> FindByIdForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<Fault?>(null);

        public Task<Fault?> FindByIdAsync(Guid id, string tenantId, CancellationToken cancellationToken) => Task.FromResult<Fault?>(null);
    }

    private sealed class InMemoryTriageJobRepository : ITriageJobRepository
    {
        public Task<TriageJob> InsertPendingAsync(Guid faultId, string configHash, CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new TriageJob(
                Id: Guid.NewGuid(),
                FaultId: faultId,
                Status: TriageJobStatus.Pending,
                Attempt: 1,
                LockedBy: null,
                LockedUntilUtc: null,
                NextAttemptAtUtc: null,
                LastErrorCode: null,
                LastErrorMessage: null,
                ConfigHash: configHash,
                CreatedAtUtc: now,
                UpdatedAtUtc: now));
        }

        public Task<TriageJob?> FindByFaultIdAsync(Guid faultId, CancellationToken cancellationToken) => Task.FromResult<TriageJob?>(null);
    }

    private sealed class InMemoryTriageArtifactRepository : ITriageArtifactRepository
    {
        public Task InsertAsync(TriageArtifact artifact, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReplaceJobLevelAsync(TriageArtifact artifact, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class InMemoryRecurrenceStateRepository : IRecurrenceStateRepository
    {
        public Task<RecurrenceState> RecordAsync(
            RecurrenceOccurrence occurrence,
            CancellationToken cancellationToken) => Task.FromResult(
                new RecurrenceState(0, occurrence.OccurredAtUtc, occurrence.OccurredAtUtc, null, null));
    }

    private sealed class InMemoryTriageJobRuntimeRepository : ITriageJobRuntimeRepository
    {
        public Task<TriageJob?> ClaimNextAsync(
            string workerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) => Task.FromResult<TriageJob?>(null);

        public Task<bool> RenewLeaseAsync(
            TriageJob job,
            string workerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) => Task.FromResult(true);
        public Task RecordAttemptFailureAsync(
            TriageJob job,
            string workerId,
            TriageJobAttemptFailure failure,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class InMemoryEmbeddingClient : IEmbeddingClient
    {
        public Task<EmbeddingResponse> CreateEmbeddingAsync(EmbeddingRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new EmbeddingResponse([1f], request.Model, "test", 1, request.CorrelationId));
    }

    private sealed class InMemoryMemoryRepository : IMemoryRepository
    {
        public Task<IReadOnlyList<MemorySearchMatch>> SearchAsync(MemorySearchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MemorySearchMatch>>([]);
        public Task<bool> SeedItemExistsAsync(
            string owner,
            MemorySeedItem item,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task ReconcileSeedCorpusAsync(
            MemorySeedCorpus corpus,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }


    private sealed class InMemoryTriageLedgerReader : ITriageLedgerReader
    {
        public Task<TriageBudgetLedgerUsage> ReadBudgetUsageAsync(
            TriageJob job,
            CancellationToken cancellationToken) => Task.FromResult(new TriageBudgetLedgerUsage(0, 0));

        public Task<int> CountPolicyDecisionsAsync(
            TriageJob job,
            string toolName,
            string scope,
            TriageLedgerDecision decision,
            CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<bool> HasSuccessfulToolResultAsync(
            TriageJob job,
            string toolName,
            string scope,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<IReadOnlyList<TriageLedgerEntry>> ReadByFaultIdAsync(
            Guid faultId,
            string tenantId,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TriageLedgerEntry>>([]);
    }
    private sealed class InMemoryModelCostRollupRepository : IModelCostRollupRepository
    {
        public Task<IReadOnlyList<CostRollupHour>> ReadAsync(
            string tenantId,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CostRollupHour>>([]);
    }
    private sealed class InMemoryActionApprovalReviewRepository : IActionApprovalReviewRepository
    {
        public Task<IReadOnlyList<ActionApprovalRecord>> ListAsync(
            ActionApprovalListFilter filter,
            string tenantId,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ActionApprovalRecord>>([]);

        public Task<(ActionApprovalRecord Action, IReadOnlyList<ActionApprovalProvenance> Provenance)?> FindAsync(
            Guid actionId,
            string tenantId,
            CancellationToken cancellationToken) =>
            Task.FromResult<(ActionApprovalRecord, IReadOnlyList<ActionApprovalProvenance>)?>(null);

        public Task<ActionDecisionResult> DecideAsync(
            ActionDecisionRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ActionDecisionResult(ActionDecisionOutcome.NotFound, null, null));
    }
    private sealed class InMemoryActionProposalRepository : IActionProposalRepository
    {
        public Task<ActionProposalOrigin?> FindSafeOriginAsync(
            string tenantId,
            Guid originReportId,
            CancellationToken cancellationToken) => Task.FromResult<ActionProposalOrigin?>(null);

        public Task<bool> RecordDenialAsync(
            string tenantId,
            Guid originReportId,
            string? auditedToolId,
            string reasonCode,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<ActionProposalResult> CreateAsync(
            PreparedActionProposal proposal,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ActionProposalResult> CreateGovernedAsync(
            GovernedActionProposal proposal,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class InMemoryTriageReportReadRepository : ITriageReportReadRepository
    {
        public Task<TriageReportDetailsResponse?> FindByIdAsync(Guid reportId, string tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<TriageReportDetailsResponse?>(null);

        public Task<TriageReportDetailsResponse?> FindLatestByFaultIdAsync(Guid faultId, string tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<TriageReportDetailsResponse?>(null);
    }

    private sealed class InMemoryTriageReportListRepository : ITriageReportListRepository
    {
        public Task<IReadOnlyList<TriageReportListItemResponse>> ListAsync(
            TriageReportListFilter filter,
            string tenantId,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TriageReportListItemResponse>>([]);
    }
    private sealed class InMemoryPriorReportSummaryProvider : IPriorReportSummaryProvider
    {
        public Task<PriorReportSummary?> FindLatestAsync(Guid faultId, CancellationToken cancellationToken) =>
            Task.FromResult<PriorReportSummary?>(null);
    }
}
