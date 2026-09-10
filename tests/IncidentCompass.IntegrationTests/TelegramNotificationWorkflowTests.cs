using System.Text;
using System.Text.Json.Nodes;
using IncidentCompass.Application;
using IncidentCompass.Application.Core.Dispatching;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.ActionApprovals.Propose;
using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Notifications;
using IncidentCompass.Infrastructure;
using IncidentCompass.Infrastructure.Notifications.Telegram;
using IncidentCompass.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IncidentCompass.IntegrationTests;

public sealed class TelegramNotificationWorkflowTests
{
    [Fact]
    public async Task SelectionUsesOneFirstMatchingBackendRoute()
    {
        var configuration = Configuration([
            new NotificationRoute("first", TelegramNotificationWorkflow.ToolIdValue, "checkout", "production", ["critical"]),
            new NotificationRoute("second", TelegramNotificationWorkflow.ToolIdValue, null, null, ["critical"])
        ]);
        using var services = Services(configuration, out _);
        var workflow = new TelegramNotificationWorkflow(
            services.GetRequiredService<ITriageConfigurationRepository>(),
            services.GetRequiredService<IServiceScopeFactory>());

        var selected = await workflow.SelectAsync(
            "tenant", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, configuration.ConfigHash,
            "CHECKOUT", "Production", "CRITICAL", TestContext.Current.CancellationToken);

        Assert.True(selected.ShouldEnqueue);
        Assert.Equal("first", selected.RouteId);
    }

    [Theory]
    [InlineData("warning", "checkout", "production")]
    [InlineData("critical", "billing", "production")]
    [InlineData("critical", "checkout", "staging")]
    public async Task SelectionRejectsThresholdAndSelectorMisses(
        string severity,
        string service,
        string environment)
    {
        var configuration = Configuration([
            new NotificationRoute("first", TelegramNotificationWorkflow.ToolIdValue, "checkout", "production", ["critical"])
        ]);
        using var services = Services(configuration, out _);
        var workflow = new TelegramNotificationWorkflow(
            services.GetRequiredService<ITriageConfigurationRepository>(),
            services.GetRequiredService<IServiceScopeFactory>());

        var selected = await workflow.SelectAsync(
            "tenant", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, configuration.ConfigHash,
            service, environment, severity, TestContext.Current.CancellationToken);

        Assert.False(selected.ShouldEnqueue);
        Assert.Null(selected.RouteId);
    }

    [Fact]
    public async Task EvaluationProposesOnlyBackendOriginAndFrozenRoute()
    {
        var configuration = Configuration([
            new NotificationRoute("telegram_ops", TelegramNotificationWorkflow.ToolIdValue, null, null, ["error"])
        ]);
        using var services = Services(configuration, out var dispatcher);
        var workflow = new TelegramNotificationWorkflow(
            services.GetRequiredService<ITriageConfigurationRepository>(),
            services.GetRequiredService<IServiceScopeFactory>());
        var reportId = Guid.NewGuid();
        var input = new JsonObject
        {
            ["originReportId"] = reportId.ToString("N"),
            ["routeId"] = "telegram_ops",
            ["toolId"] = TelegramNotificationWorkflow.ToolIdValue,
            ["workflowVersion"] = 1
        };
        var intent = new PostReportActionIntent(
            Guid.NewGuid(), "tenant", reportId, Guid.NewGuid(), Guid.NewGuid(), 1,
            TelegramNotificationWorkflow.ToolIdValue, 1, "telegram_ops", configuration.ConfigHash,
            $"post-report:v1:{reportId:N}:{TelegramNotificationWorkflow.ToolIdValue}",
            Encoding.UTF8.GetBytes(CanonicalJsonSerializer.Canonicalize(input)),
            PostReportActionIntentState.Processing, "owner", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1),
            1, null, null, DateTimeOffset.UtcNow, null);

        var result = await workflow.EvaluateAsync(intent, TestContext.Current.CancellationToken);

        Assert.True(result.IsCompleted);
        var request = Assert.IsType<ProposePostReportActionCommand>(dispatcher.Request);
        Assert.Equal(reportId, request.OriginReportId);
        Assert.Equal(TelegramNotificationWorkflow.ToolIdValue, request.ToolId);
        Assert.Equal("telegram_ops", request.Arguments.GetProperty("routeId").GetString());
        Assert.Equal(2, request.Arguments.EnumerateObject().Count());
    }

    [Fact]
    public void WorkerCompositionRegistersAdapterWhileApiRegistersDescriptorOnly()
    {
        using var worker = BuildHost(includeWorker: true);
        using var workerScope = worker.Services.CreateScope();
        var workerRegistry = workerScope.ServiceProvider.GetRequiredService<IAgentToolRegistry>();
        Assert.True(workerRegistry.TryGet(TelegramNotificationWorkflow.ToolIdValue, out var descriptor));
        Assert.Equal(TelegramNotificationWorkflow.LogicalTargetIdValue, descriptor.LogicalTargetId);
        Assert.Contains(
            workerScope.ServiceProvider.GetRequiredService<PostReportActionWorkflowCatalog>().Workflows,
            workflow => workflow is TelegramNotificationWorkflow);
        Assert.IsType<TelegramNotificationActionTool>(
            workerScope.ServiceProvider.GetServices<IExternalActionTool>().Single(tool =>
                tool.Definition.Name == TelegramNotificationWorkflow.ToolIdValue));

        using var api = BuildHost(includeWorker: false);
        using var apiScope = api.Services.CreateScope();
        var apiRegistry = apiScope.ServiceProvider.GetRequiredService<IAgentToolRegistry>();
        Assert.True(apiRegistry.TryGet(TelegramNotificationWorkflow.ToolIdValue, out var apiDescriptor));
        Assert.Equal(TelegramNotificationWorkflow.LogicalTargetIdValue, apiDescriptor.LogicalTargetId);
        Assert.DoesNotContain(
            apiScope.ServiceProvider.GetRequiredService<PostReportActionWorkflowCatalog>().Workflows,
            workflow => workflow.ToolId == TelegramNotificationWorkflow.ToolIdValue);
        Assert.DoesNotContain(
            apiScope.ServiceProvider.GetServices<IExternalActionTool>(),
            tool => tool.Definition.Name == TelegramNotificationWorkflow.ToolIdValue);
    }

    private static ServiceProvider Services(
        TriageConfiguration configuration,
        out RecordingDispatcher dispatcher)
    {
        dispatcher = new RecordingDispatcher();
        var services = new ServiceCollection();
        services.AddSingleton<ITriageConfigurationRepository>(new StaticNotificationConfiguration(configuration));
        services.AddSingleton<IApplicationDispatcher>(dispatcher);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static IHost BuildHost(bool includeWorker)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["IncidentCompass:ModelGateway:Provider"] = "Mock",
            ["IncidentCompass:Embeddings:Provider"] = "Mock"
        });
        builder.Services.AddApplication(builder.Configuration);
        builder.Services.AddInfrastructure(builder.Configuration);
        if (includeWorker)
        {
            builder.Services.AddWorker(builder.Configuration);
        }

        return builder.Build();
    }

    private static TriageConfiguration Configuration(IReadOnlyList<NotificationRoute> routes) => new(
        "telegram-config",
        new Dictionary<string, TriageProviderSettings>(StringComparer.Ordinal),
        new Dictionary<string, TriageRouteSettings>(StringComparer.Ordinal),
        new OrchestratorSettings("orchestrator", "chat", ["delegate", "publish_report"],
            new OrchestratorBudgetSettings(1, 1000, 30)),
        new Dictionary<string, TriageRoleSettings>(StringComparer.Ordinal),
        new Dictionary<string, TriageToolSettings>(StringComparer.Ordinal)
        {
            [TelegramNotificationWorkflow.ToolIdValue] = new(
                "external_action", null, null, null, "notification",
                TelegramNotificationWorkflow.LogicalTargetIdValue)
        },
        [],
        new IngestionSettings("tenant", ["tester"]),
        new FaultGroupingSettings(15, 30, 1, new MassIssueSettings(5, "strong")),
        RedactionSettings.Default)
    {
        Actions = new TriageActionSettings(
            [TelegramNotificationWorkflow.ToolIdValue], "live", false, 60)
        {
            NotificationRoutes = routes
        }
    };
}

internal sealed class StaticNotificationConfiguration(TriageConfiguration configuration)
    : ITriageConfigurationRepository
{
    public Task<TriageConfiguration> GetCurrentAsync(CancellationToken cancellationToken) =>
        Task.FromResult(configuration);

    public Task<TriageConfiguration> GetByHashAsync(string configHash, CancellationToken cancellationToken) =>
        Task.FromResult(configuration with { ConfigHash = configHash });
}

internal sealed class RecordingDispatcher : IApplicationDispatcher
{
    public object? Request { get; private set; }

    public Task<TResponse> DispatchAsync<TRequest, TResponse>(
        TRequest request,
        CancellationToken cancellationToken = default)
        where TRequest : IRequest<TResponse>
    {
        Request = request;
        object response = new PostReportActionProposalResponse(
            PostReportActionProposalOutcome.Denied, "recorded", null, false, true);
        return Task.FromResult((TResponse)response);
    }
}
