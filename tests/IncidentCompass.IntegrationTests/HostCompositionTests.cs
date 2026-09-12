using IncidentCompass.Application.Core.Configuration;
using IncidentCompass.Application.Core.Security;
using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Retention;
using IncidentCompass.Application.Investigation.Retention;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Infrastructure;
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

        // Investigation/action/retention workers + Infrastructure warmups for config and optional
        // memory seeding.
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        Assert.Equal(9, hostedServices.Length);
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
