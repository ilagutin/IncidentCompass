using IncidentCompass.Application.Core.Configuration;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.Security;
using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Retention;
using IncidentCompass.Application.Investigation.Retention;
using IncidentCompass.Application.Memory;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Infrastructure;
using IncidentCompass.Infrastructure.Memory;
using IncidentCompass.Infrastructure.Relevance;
using IncidentCompass.Infrastructure.Relevance.Mock;
using IncidentCompass.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WorkerService = IncidentCompass.Worker.Worker;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// What the Worker host is made of: the hosted services it ends up running, the identity a
/// background job resolves, the registrations a worker needs from a scope of its own, and the two
/// composition mistakes that must be refused rather than discovered at run time. These build the
/// container the real Worker builds, with scope validation on, so a registration that is only
/// resolvable by accident fails here.
/// </summary>
[Collection(HostCompositionCollection.CollectionName)]
public sealed class HostCompositionTests
{
    [Fact]
    public void WorkerHostServices_CanBuildWithScopeValidation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IncidentCompass:Application:ApiVersion"] = "v1",
                ["IncidentCompass:Postgres:ConnectionStringName"] = "IncidentCompass"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);
        services.AddWorker(configuration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        // Investigation/action/retention workers + the Infrastructure config warmup + the three the
        // Worker's local model host brings: the embedding model's install pass, the optional memory
        // seeding, and the relevance judge's own install pass, which verifies the cross-encoder that
        // reranks memory search on the one host composed with it.
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        Assert.Equal(11, hostedServices.Length);
        Assert.Contains(hostedServices, service =>
            service.GetType().FullName == "IncidentCompass.Worker.TelegramConfigurationStartupValidator");
        Assert.Contains(hostedServices, service =>
            service.GetType().FullName == "IncidentCompass.Worker.GitHubIssueConfigurationStartupValidator");
        Assert.Contains(hostedServices, service =>
            service.GetType().FullName == "IncidentCompass.Worker.CodePublicationConfigurationStartupValidator");
        Assert.Contains(hostedServices, service => service is WorkerService);
        Assert.Contains(hostedServices, service =>
            service.GetType().FullName == "IncidentCompass.Worker.ActionDispatchWorker");
        Assert.Contains(hostedServices, service =>
            service.GetType().FullName == "IncidentCompass.Worker.PostReportActionEvaluationWorker");
        Assert.Contains(hostedServices, service =>
            service.GetType().FullName == "IncidentCompass.Worker.RetentionWorker");
        Assert.Contains(hostedServices, service =>
            service.GetType().FullName == "IncidentCompass.Infrastructure.Intake.TriageConfigurationWarmupHostedService");
        Assert.Contains(hostedServices, service =>
            service.GetType().FullName == "IncidentCompass.Infrastructure.Memory.MemorySeedHostedService");
        Assert.Contains(hostedServices, service =>
            service.GetType().FullName == ModelInstallHostedServiceTypeName);
        Assert.Contains(hostedServices, service =>
            service.GetType().FullName == RelevanceJudgeInstallHostedServiceTypeName);

        // The generic host starts hosted services in resolution order, and the order of these three
        // is a decision, not an accident. The embedding model's install runs first, so the first seed
        // pass finds the model installed or finds its named failure. The judge's install runs last of
        // the three: nothing on the seed path scores relevance, and the judge is by far the larger
        // download, so starting it earlier would hold the corpus the Worker actually serves behind a
        // model no pass has asked for yet. The assertion is relative to these three only, not a
        // position in the whole list, so adding an unrelated worker does not break it.
        var modelInstallIndex = IndexOfHostedService(hostedServices, ModelInstallHostedServiceTypeName);
        var memorySeedIndex = IndexOfHostedService(hostedServices, MemorySeedHostedServiceTypeName);
        var judgeInstallIndex = IndexOfHostedService(hostedServices, RelevanceJudgeInstallHostedServiceTypeName);
        Assert.True(
            modelInstallIndex < memorySeedIndex,
            "The memory seed pass would start before the local embedding model install.");
        Assert.True(
            memorySeedIndex < judgeInstallIndex,
            "The relevance judge install would start before the first memory seed pass.");
        using var scope = provider.CreateScope();
        var backgroundContext = scope.ServiceProvider.GetRequiredService<IBackgroundUserContext>();
        var userContext = scope.ServiceProvider.GetRequiredService<IUserContext>();
        Assert.Same(backgroundContext, userContext);
        Assert.True(userContext.IsAuthenticated);
        Assert.Equal("system", userContext.UserId);
        Assert.Null(userContext.TenantId);
        Assert.Contains("system", userContext.Roles);
        Assert.Contains(scope.ServiceProvider.GetServices<IPostReportActionWorkflow>(),
            workflow => workflow.ToolId == TicketCreateTool.ToolId);
        Assert.Contains(scope.ServiceProvider.GetServices<IPostReportActionWorkflow>(),
            workflow => workflow.ToolId == TicketUpdatePostReportActionWorkflow.UpdateToolId);
        Assert.Contains(scope.ServiceProvider.GetServices<IExternalActionTool>(),
            tool => tool.Definition.Name == TicketCreateTool.ToolId);
        Assert.Contains(scope.ServiceProvider.GetServices<IExternalActionTool>(),
            tool => tool.Definition.Name == TicketUpdatePostReportActionWorkflow.UpdateToolId);
    }

    /// <summary>
    /// The retention operations are Application types registered by <c>AddInfrastructure</c>, because
    /// neither can be constructed without the persistence port its adapter supplies. The only thing
    /// that resolves them is <c>RetentionPump</c>, and it does so from a scope of its own at run time,
    /// so without this the wiring could be wrong in either direction and every other test would still
    /// pass. The pump is resolved here too, from the same provider the Worker host builds, because a
    /// singleton that takes a scope factory would not catch a missing scoped registration at build
    /// time either.
    /// </summary>
    [Fact]
    public void WorkerHostServices_ResolveBothRetentionOperations()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IncidentCompass:Application:ApiVersion"] = "v1",
                ["IncidentCompass:Postgres:ConnectionStringName"] = "IncidentCompass",
                ["IncidentCompass:Retention:SignalPayloadRetentionDays"] = "45",
                ["IncidentCompass:Retention:AttemptArtifactRetentionDays"] = "3",
                ["IncidentCompass:Retention:MaxRowsPerRun"] = "250",
                ["IncidentCompass:RetentionSchedule:IntervalMinutes"] = "30"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);
        services.AddWorker(configuration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AgedSignalPayloadCompactor>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<StaleAttemptArtifactReaper>());
        Assert.NotNull(provider.GetRequiredService<RetentionPump>());
        var retention = scope.ServiceProvider.GetRequiredService<IOptions<RetentionOptions>>().Value;
        Assert.Equal(45, retention.SignalPayloadRetentionDays);
        Assert.Equal(3, retention.AttemptArtifactRetentionDays);
        Assert.Equal(250, retention.MaxRowsPerRun);
        var schedule = provider.GetRequiredService<IOptions<RetentionScheduleOptions>>().Value;
        Assert.True(schedule.Enabled);
        Assert.Equal(30, schedule.IntervalMinutes);
    }

    /// <summary>
    /// The other side of the embedding boundary. <c>AddInfrastructure</c> without <c>AddWorker</c> is
    /// what the Api composes, and it carries no embedding client, no memory seed pass and no
    /// <c>memory_search</c> tool, while the corpus status and health readers the Api serves stay. The
    /// same collection gains all three once <c>AddWorker</c> runs, so they are shown to arrive through
    /// the Worker rather than merely to be missing.
    /// </summary>
    [Fact]
    public void InfrastructureWithoutWorker_ComposesNoEmbeddingModel()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IncidentCompass:Application:ApiVersion"] = "v1",
                ["IncidentCompass:Postgres:ConnectionStringName"] = "IncidentCompass"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IEmbeddingClient));
        Assert.DoesNotContain(services, IsMemorySeedHostedService);
        Assert.DoesNotContain(services, IsModelInstallHostedService);
        Assert.DoesNotContain(services, IsMemorySearchTool);
        Assert.Equal(
            typeof(MemoryCorpusStatusReader),
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IMemoryCorpusStatusReader)).ImplementationType);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IMemorySeedSyncStatusReader));

        services.AddWorker(configuration);

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IEmbeddingClient));
        Assert.Single(services, IsModelInstallHostedService);
        Assert.Single(services, IsMemorySeedHostedService);
        Assert.Single(services, IsMemorySearchTool);

        // The Worker judges the corpus against the installed local model, so its status reader
        // replaces the Api's rather than sitting beside it.
        Assert.Equal(
            typeof(WorkerMemoryCorpusStatusReader),
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IMemoryCorpusStatusReader)).ImplementationType);
    }

    /// <summary>
    /// <c>IncidentCompass:RelevanceJudge:Provider</c> set to <c>Mock</c> gives the Worker the mock
    /// stack's deterministic judge, and nothing of the local judge: no install pass, so a mock host
    /// downloads no cross-encoder whatever judge directory it inherits, and it logs at start that it
    /// runs the mock. Everything else the Worker runs is unchanged, so the hosted-service count is the
    /// default composition's with that one pass exchanged for the start-up warning.
    /// </summary>
    [Fact]
    public void WorkerHostServices_WithTheMockJudgeProvider_ComposeTheMockJudgeAndNoJudgeInstallPass()
    {
        var configuration = WorkerConfiguration(RelevanceJudgeOptions.MockProvider);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);
        services.AddWorker(configuration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        Assert.Equal(11, hostedServices.Length);
        Assert.DoesNotContain(hostedServices, service =>
            service.GetType().FullName == RelevanceJudgeInstallHostedServiceTypeName);
        Assert.Single(hostedServices, service => service is MockRelevanceJudgeStartupWarning);
        Assert.IsType<MockMemoryRelevanceJudge>(provider.GetRequiredService<IMemoryRelevanceJudge>());
        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    /// <summary>
    /// A provider that names no judge stops the Worker at start with a named failure, instead of
    /// quietly composing the local judge in its place.
    /// </summary>
    [Theory]
    [InlineData("OpenAICompatible")]
    [InlineData("Onnx")]
    public void WorkerHostServices_WithAnUnsupportedJudgeProvider_FailAtStart(string providerName)
    {
        var configuration = WorkerConfiguration(providerName);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);
        services.AddWorker(configuration);
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<RelevanceJudgeOptions>>().Value);

        Assert.Contains(RelevanceJudgeOptions.ProviderKey, exception.Message, StringComparison.Ordinal);
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    private static IConfiguration WorkerConfiguration(string judgeProvider) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IncidentCompass:Application:ApiVersion"] = "v1",
                ["IncidentCompass:Postgres:ConnectionStringName"] = "IncidentCompass",
                [RelevanceJudgeOptions.ProviderKey] = judgeProvider
            })
            .Build();

    /// <summary>
    /// The real Api host, built through its own <c>Program</c>, resolves neither of the in-process
    /// models, starts no memory seed pass and no model install pass of either kind, offers no
    /// <c>memory_search</c> tool, and still resolves the corpus status reader its health route serves.
    /// </summary>
    [Fact]
    public void ApiHost_ResolvesNoLocalModelOrMemorySeedPass()
    {
        using var factory = new MockProvidersWebApplicationFactory();
        using var scope = factory.Services.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<IEmbeddingClient>());
        Assert.Null(scope.ServiceProvider.GetService<IMemoryRelevanceJudge>());
        Assert.DoesNotContain(scope.ServiceProvider.GetServices<IImmediateAgentTool>(), tool =>
            tool.GetType().FullName == MemorySearchToolTypeName);
        foreach (var typeName in new[]
                 {
                     MemorySeedHostedServiceTypeName,
                     ModelInstallHostedServiceTypeName,
                     RelevanceJudgeInstallHostedServiceTypeName
                 })
        {
            Assert.DoesNotContain(factory.Services.GetServices<IHostedService>(), service =>
                service.GetType().FullName == typeName);
        }

        Assert.NotNull(scope.ServiceProvider.GetService<IMemoryCorpusStatusReader>());
    }

    [Fact]
    public void WorkerHostServices_RejectMissingInfrastructureWiring()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IncidentCompass:Application:ApiVersion"] = "v1"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddWorker(configuration));

        Assert.Contains("deferred placeholder", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AddInfrastructure", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerHostServices_RejectInvalidPostReportWorkflowCatalogAtStartup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IncidentCompass:Application:ApiVersion"] = "v1",
                ["IncidentCompass:Postgres:ConnectionStringName"] = "IncidentCompass"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);
        services.AddSingleton<IPostReportActionWorkflow, InvalidPostReportActionWorkflow>();
        services.AddWorker(configuration);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetServices<IHostedService>().ToArray());

        Assert.Contains("does not match its backend descriptor", exception.Message);
    }

    private const string MemorySeedHostedServiceTypeName = "IncidentCompass.Infrastructure.Memory.MemorySeedHostedService";

    private const string ModelInstallHostedServiceTypeName =
        "IncidentCompass.Infrastructure.EmbeddingModels.LocalOnnxModelInstallHostedService";

    private const string RelevanceJudgeInstallHostedServiceTypeName =
        "IncidentCompass.Infrastructure.Relevance.LocalOnnx.LocalOnnxRelevanceJudgeInstallHostedService";

    private const string MemorySearchToolTypeName = "IncidentCompass.Application.Memory.MemorySearchTool";

    private static int IndexOfHostedService(IHostedService[] hostedServices, string typeName) =>
        Array.FindIndex(hostedServices, service => service.GetType().FullName == typeName);

    private static bool IsMemorySeedHostedService(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(IHostedService) &&
        !descriptor.IsKeyedService &&
        descriptor.ImplementationType?.FullName == MemorySeedHostedServiceTypeName;

    private static bool IsModelInstallHostedService(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(IHostedService) &&
        !descriptor.IsKeyedService &&
        descriptor.ImplementationType?.FullName == ModelInstallHostedServiceTypeName;

    private static bool IsMemorySearchTool(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(IImmediateAgentTool) &&
        !descriptor.IsKeyedService &&
        descriptor.ImplementationType?.FullName == MemorySearchToolTypeName;

    private sealed class InvalidPostReportActionWorkflow : IPostReportActionWorkflow
    {
        public string ToolId => "missing_action";

        public int WorkflowVersion => 1;

        public ActionCategory Category => ActionCategory.Notification;

        public string LogicalTargetId => "missing:target";

        public Task<(bool ShouldEnqueue, string? RouteId)> SelectAsync(
            string tenantId,
            Guid originReportId,
            Guid faultId,
            Guid jobId,
            int attempt,
            string configHash,
            string serviceName,
            string environment,
            string? severity,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PostReportActionWorkflowResult> EvaluateAsync(
            PostReportActionIntent intent,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
