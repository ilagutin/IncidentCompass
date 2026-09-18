using IncidentCompass.Application;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Application.Core.Security;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.ActionApprovals.Testing;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Application.Governance.PostReportActions.Testing;
using IncidentCompass.Application.Intake.Retention;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Application.Investigation.Reports.Context;
using IncidentCompass.Application.Investigation.Reports.Fallback;
using IncidentCompass.Application.Investigation.Reports.List;
using IncidentCompass.Application.Investigation.Reports.Redaction;
using IncidentCompass.Application.Investigation.Retention;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.Infrastructure.Embeddings.LocalOnnx;
using IncidentCompass.Infrastructure.Embeddings.Mock;
using IncidentCompass.Infrastructure.Embeddings.OpenAi;
using IncidentCompass.Infrastructure.Governance;
using IncidentCompass.Infrastructure.Governance.ActionApprovals;
using IncidentCompass.Infrastructure.Governance.PostReportActions;
using IncidentCompass.Infrastructure.Intake;
using IncidentCompass.Infrastructure.Investigation;
using IncidentCompass.Infrastructure.Memory;
using IncidentCompass.Infrastructure.ModelGateway.Mock;
using IncidentCompass.Infrastructure.ModelGateway.OpenAi;
using IncidentCompass.Infrastructure.Observability;
using IncidentCompass.Infrastructure.OpenAiCompatible;
using IncidentCompass.Infrastructure.Postgres;
using IncidentCompass.Infrastructure.Postgres.Testing;
using IncidentCompass.Infrastructure.Relevance.LocalOnnx;
using IncidentCompass.Infrastructure.Remediation;
using IncidentCompass.Infrastructure.Security;
using IncidentCompass.Infrastructure.SourceContext;
using IncidentCompass.Infrastructure.Tickets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure;

public static class Setup
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddInfrastructureOptions(configuration);
        services.AddModelGatewayAdapters();
        services.AddGovernedInvestigationServices();
        services.Replace(ServiceDescriptor.Scoped<IClaimedTriageJobProcessor, GovernedTriageInvestigationProcessor>());
        services.AddObservabilityInfrastructure(configuration);
        services.AddPersistenceAdapters();
        services.AddRetentionOperations();
        services.AddIntakeInfrastructure(configuration);
        services.AddMemoryInfrastructure(configuration);
        services.AddSourceContextInfrastructure(configuration);
        services.AddRemediationInfrastructure(configuration);
        services.AddTicketInfrastructure(configuration);
        // Infrastructure supplies the Worker identity; API auth binds IUserContext explicitly.
        services.TryAddScoped<IBackgroundUserContext, SystemUserContext>();
        return services;
    }

    public static IServiceCollection AddPostgresMigrations(this IServiceCollection services)
    {
        // Test fault seam, not a real service: the no-op default lets PostgresMigrationRunner call
        // the seam that lets integration tests fail one chosen migration version mid-catalog. It is
        // registered here rather than with the other seams because AddPostgresMigrations is a
        // standalone host entry point, so the runner must be able to resolve it on its own.
        services.TryAddSingleton<IPostgresMigrationFailureInjector, NoPostgresMigrationFailureInjector>();
        services.TryAddSingleton<PostgresMigrationReadiness>();
        services.TryAddSingleton<IPostgresMigrationReadiness>(
            serviceProvider => serviceProvider.GetRequiredService<PostgresMigrationReadiness>());
        services.TryAddSingleton<PostgresMigrationRunner>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PostgresMigrationHostedService>());

        return services;
    }

    /// <summary>
    /// The local model host: the provider-selected <see cref="IEmbeddingClient" /> and its options,
    /// the local model store and its install pass, the memory seed and resync pass, the synchronizer
    /// the <c>memory rebuild</c> command runs, the <c>memory_search</c> tool and the local relevance
    /// judge that reranks what that tool retrieves. It is not part of
    /// <see cref="AddInfrastructure" /> because one process owns the embedding model, and that is the
    /// Worker, whose <c>AddWorker</c> calls it. The Api keeps the corpus status and health readers,
    /// none of which embeds anything. The client, the synchronizer, the hosted services and the tool
    /// are TryAdd registrations, so a client bound before this call wins and a second call adds no
    /// second hosted service or tool.
    /// <para>
    /// The local model install hosted service is registered before the memory seed hosted service.
    /// The generic host starts hosted services in registration order, so the first seed pass finds the
    /// local model installed or finds its named failure.
    /// </para>
    /// </summary>
    public static IServiceCollection AddEmbeddingHost(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddEmbeddingOptions(configuration);
        services.AddEmbeddingAdapters();
        services.AddLocalOnnxModelStore(configuration);
        services.TryAddScoped<IMemoryChunkTokenCounter>(provider =>
            ProviderKindParser.IsLocalOnnx(provider.GetRequiredService<IOptions<EmbeddingOptions>>().Value.Provider)
                ? new LocalOnnxChunkTokenCounter(
                    provider.GetRequiredService<LocalOnnxInstalledModelReader>(),
                    provider.GetRequiredService<LocalOnnxModelRuntime>())
                : new CharacterEstimateChunkTokenCounter());

        // The Worker judges the corpus against the installed local model; the Api cannot, so the
        // Worker's status reader replaces the one AddInfrastructure binds for both hosts.
        services.TryAddSingleton<WorkerMemoryEmbeddingRouteResolver>();
        services.Replace(ServiceDescriptor.Scoped<IMemoryCorpusStatusReader, WorkerMemoryCorpusStatusReader>());
        services.TryAddScoped<MemorySeedSynchronizer>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MemorySeedHostedService>());
        services.AddMemorySearchTool();

        // Last, and after the memory seed pass, so the judge's own install pass starts behind the
        // embedding model's install and behind the first seed pass. See AddLocalOnnxRelevanceJudge.
        services.AddLocalOnnxRelevanceJudge(configuration);

        return services;
    }

    private static IServiceCollection AddGovernedInvestigationServices(this IServiceCollection services)
    {
        services.TryAddScoped<TriageLedgerAppender>();
        services.TryAddScoped<InvestigationModelCaller>();
        services.TryAddScoped<WorkerToolCallExecutor>();
        services.TryAddScoped<WorkerRoleRunner>();
        services.TryAddScoped<AnalysisDelegateExecutor>();
        services.TryAddScoped<TriageReportPublisher>();
        return services;
    }

    /// <summary>
    /// Binds the two retention operations here rather than in <c>AddApplication</c>. Both are
    /// Application types, but neither can be constructed without the persistence port its adapter
    /// supplies, and <c>AddApplication</c> has to stay resolvable on its own: the memory-only path
    /// composes Application without any of this. Registering them next to the adapters keeps the
    /// operation and the only thing that can satisfy it in one place - the same reason
    /// <see cref="AddGovernedInvestigationServices" /> lives here.
    /// </summary>
    private static IServiceCollection AddRetentionOperations(this IServiceCollection services)
    {
        services.TryAddScoped<AgedSignalPayloadCompactor>();
        services.TryAddScoped<StaleAttemptArtifactReaper>();
        return services;
    }

    private static IServiceCollection AddInfrastructureOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddPostgresConnectionOptions(configuration);
        services
            .AddOptions<ModelGatewayOptions>()
            .Bind(configuration.GetSection(ModelGatewayOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<ModelGatewayOptions>,
            ModelGatewayProviderOptionsValidator>());
        services
            .AddOptions<OpenAiCompatibleModelClientOptions>()
            .Bind(configuration.GetSection(OpenAiCompatibleModelClientOptions.SectionName))
            .Validate<IOptions<ModelGatewayOptions>>(
                static (openAiOptions, modelGatewayOptions) =>
                    !ProviderKindParser.IsOpenAiCompatible(modelGatewayOptions.Value.Provider) ||
                    openAiOptions.IsValid(),
                "OpenAI-compatible model gateway configuration is invalid.")
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<OpenAiCompatibleModelClientOptions>,
            OpenAiCompatibleModelClientOptionsValidator>());

        return services;
    }

    private static IServiceCollection AddEmbeddingOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<EmbeddingOptions>()
            .Bind(configuration.GetSection(EmbeddingOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<EmbeddingOptions>,
            EmbeddingProviderOptionsValidator>());
        services
            .AddOptions<OpenAiCompatibleEmbeddingClientOptions>()
            .Bind(configuration.GetSection(OpenAiCompatibleEmbeddingClientOptions.SectionName))
            .Validate<IOptions<EmbeddingOptions>>(
                static (openAiOptions, embeddingOptions) =>
                    !ProviderKindParser.IsOpenAiCompatible(embeddingOptions.Value.Provider) ||
                    openAiOptions.IsValid(),
                "OpenAI-compatible embedding provider configuration is invalid.")
            .ValidateOnStart();

        return services;
    }

    /// <summary>
    /// The provider-selection collaborators both OpenAI-compatible adapters share. They are
    /// registered once, beside the adapters rather than inside either one's registration, because a
    /// route's provider must resolve to the same endpoint and credential whether the call is a chat
    /// completion or an embedding.
    /// </summary>
    private static IServiceCollection AddProviderProfileResolution(this IServiceCollection services)
    {
        services.TryAddSingleton<IModelProviderSecretReader, EnvironmentModelProviderSecretReader>();
        services.TryAddScoped<OpenAiCompatibleProviderProfileResolver>();
        return services;
    }

    private static IServiceCollection AddModelGatewayAdapters(this IServiceCollection services)
    {
        services.AddProviderProfileResolution();

        // AddHttpClient registers the typed client itself; the mock has no HTTP dependency and is
        // registered directly. Both stay concrete-type registrations so the selector below can pick
        // one without a second factory.
        services
            .AddHttpClient<OpenAiCompatibleModelClient>(client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.MaxResponseContentBufferSize = OpenAiCompatibleModelClient.MaxResponseContentBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(OpenAiCompatiblePrimaryHandlerFactory.Create);
        services.TryAddScoped<MockAiModelClient>();

        // There is no local chat adapter. ModelGatewayProviderOptionsValidator refuses LocalOnnx
        // while the host starts, so this arm is unreachable on a started host; it throws rather than
        // resolving some other client if a container is ever resolved without start-up validation.
        return services.AddProviderSelectedClient<
            IAiModelClient, ModelGatewayOptions, MockAiModelClient, OpenAiCompatibleModelClient>(
            static options => options.Provider,
            static provider => $"Unsupported model gateway provider '{provider}'.",
            static _ => throw new InvalidOperationException(
                "Model gateway provider 'LocalOnnx' is an embedding-only provider with no chat adapter."));
    }

    private static IServiceCollection AddEmbeddingAdapters(this IServiceCollection services)
    {
        services.AddProviderProfileResolution();

        services.AddHttpClient<OpenAiCompatibleEmbeddingClient>(client =>
            client.Timeout = Timeout.InfiniteTimeSpan);
        services.TryAddScoped<MockEmbeddingClient>();
        services.TryAddScoped<LocalOnnxEmbeddingClient>();
        services.TryAddSingleton<LocalOnnxModelRuntime>();

        return services.AddProviderSelectedClient<
            IEmbeddingClient, EmbeddingOptions, MockEmbeddingClient, OpenAiCompatibleEmbeddingClient>(
            static options => options.Provider,
            static provider => $"Unsupported embedding provider '{provider}'.",
            static serviceProvider => serviceProvider.GetRequiredService<LocalOnnxEmbeddingClient>());
    }

    /// <summary>
    /// The single provider-selection path shared by the model gateway and the embedding gateway.
    /// The switch deliberately has no discard arm. CS8524 (the "unnamed enum value" half of switch
    /// exhaustiveness) is suppressed for it, while CS8509 (a declared <see cref="ProviderKind"/>
    /// member is not handled) stays on and is an error under TreatWarningsAsErrors, so adding a
    /// fourth provider kind breaks the build here instead of falling through at runtime.
    /// The suppressed case cannot arise: the only value reaching the switch comes from
    /// <c>ProviderKindParser.TryParse</c>, which returns <see langword="true"/> only for a declared
    /// member, and an unparsed provider string has already thrown above. A discard arm would trade
    /// that build-time failure for an <see cref="InvalidOperationException"/> raised while the
    /// container resolves the client, which is a 500 in the Api and a failing Worker claim loop.
    /// <para>
    /// <see cref="ProviderKind.LocalOnnx"/> is the one kind the two gateways do not share, so its arm
    /// is a per-gateway factory rather than a third client type: the embedding gateway resolves its
    /// local adapter, and the model gateway passes a factory that throws, which start-up validation
    /// keeps unreachable because <c>ModelGatewayProviderOptionsValidator</c> refuses that kind.
    /// </para>
    /// </summary>
    private static IServiceCollection AddProviderSelectedClient<TClient, TOptions, TMock, TOpenAiCompatible>(
        this IServiceCollection services,
        Func<TOptions, string?> providerAccessor,
        Func<string?, string> unsupportedProviderMessage,
        Func<IServiceProvider, TClient> resolveLocalOnnxClient)
        where TClient : class
        where TOptions : class
        where TMock : class, TClient
        where TOpenAiCompatible : class, TClient
    {
        services.TryAddScoped<TClient>(serviceProvider =>
        {
            var provider = providerAccessor(
                serviceProvider.GetRequiredService<IOptions<TOptions>>().Value);

            if (!ProviderKindParser.TryParse(provider, out var providerKind))
            {
                throw new InvalidOperationException(unsupportedProviderMessage(provider));
            }

#pragma warning disable CS8524
            return providerKind switch
            {
                ProviderKind.Mock => serviceProvider.GetRequiredService<TMock>(),
                ProviderKind.OpenAiCompatible => serviceProvider.GetRequiredService<TOpenAiCompatible>(),
                ProviderKind.LocalOnnx => resolveLocalOnnxClient(serviceProvider)
            };
#pragma warning restore CS8524
        });

        return services;
    }

    private static IServiceCollection AddPersistenceAdapters(this IServiceCollection services)
    {
        // TimeProvider is registered here as well as by AddApplication, with TryAdd on both sides
        // so the shared instance is bound exactly once: the startup connection wait is measured
        // with it, and AddInfrastructure must stay resolvable without AddApplication.
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<PostgresFirstConnectionRetry>();
        services.TryAddSingleton<PostgresDataSourceProvider>();
        services.TryAddScoped<ITriageLedgerWriter, PostgresTriageLedgerWriter>();
        services.TryAddScoped<ITriageLedgerReader, PostgresTriageLedgerReader>();
        services.TryAddScoped<ITriageJobInvestigationContextRepository, PostgresTriageJobInvestigationContextRepository>();
        services.TryAddScoped<PostgresDocumentationFitResolver>();
        services.TryAddScoped(serviceProvider => new PostgresReportEvidenceGrounder(
            serviceProvider.GetRequiredService<IOptions<GitHubIssuesOptions>>().Value.ConfiguredRepository));
        services.TryAddScoped<ITriageReportRepository, PostgresTriageReportRepository>();
        services.TryAddScoped<ITriageReportReadRepository, PostgresTriageReportReadRepository>();
        services.TryAddScoped<ITriageReportListRepository, PostgresTriageReportListRepository>();
        services.TryAddScoped<ITriageToolResultCommitter, PostgresTriageToolResultCommitter>();
        services.TryAddScoped<ISignalPayloadCompactionRepository, PostgresSignalPayloadCompactionRepository>();
        services.TryAddScoped<IAttemptArtifactRetentionRepository, PostgresAttemptArtifactRetentionRepository>();
        services.TryAddScoped<IReadOnlyContextOutcomeRepository, PostgresReadOnlyContextOutcomeRepository>();
        services.TryAddScoped<ICitedEvidenceRedactionRepository, PostgresCitedEvidenceRedactionRepository>();
        services.TryAddScoped<IAttemptModelFallbackRepository, PostgresAttemptModelFallbackRepository>();
        services.TryAddScoped<IActionProposalRepository, PostgresActionProposalRepository>();
        services.TryAddScoped<IActionApprovalReviewRepository, PostgresActionReviewRepository>();
        services.TryAddScoped<IActionDispatchRepository, PostgresActionDispatchRepository>();
        services.TryAddScoped<IApprovedActionDispatcher, ApprovedActionDispatcher>();
        services.TryAddSingleton<PostReportActionWorkflowCatalog>();
        services.TryAddScoped<ITriageReportPublicationIntentWriter, PostgresReportPublicationIntentWriter>();
        services.TryAddScoped<IPostReportActionIntentRepository,
            PostgresPostReportActionIntentRepository>();
        services.AddPersistenceTestFaultSeams();
        return services;
    }

    /// <summary>
    /// Binds the no-op defaults for the governance <c>Testing</c> fault seams. These are not real
    /// services: they exist so the integration tests can simulate a crash between two statements of
    /// one commit transaction, which no decorator around the outer repository port can reach.
    /// Production always gets the no-ops, so this registration is deliberately grouped and named
    /// rather than scattered among the real persistence adapters.
    /// </summary>
    private static IServiceCollection AddPersistenceTestFaultSeams(this IServiceCollection services)
    {
        services.TryAddScoped<IActionApprovalTransactionFaultInjector, NoopActionApprovalTransactionFaultInjector>();
        services.TryAddScoped<ITriageReportPublicationIntentFaultInjector,
            NoopTriageReportPublicationIntentFaultInjector>();
        return services;
    }
}
