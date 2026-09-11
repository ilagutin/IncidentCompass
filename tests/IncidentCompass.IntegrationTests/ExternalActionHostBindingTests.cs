using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Notifications;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Infrastructure;
using IncidentCompass.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// Which host is allowed to hold an external action's credential, and what happens when the host
/// binding and the public action configuration disagree. The API-shared composition gets the
/// non-secret descriptors with no credential at all; the Worker is the only host that binds one,
/// and its startup validators have to reject a mismatch without printing the secret they checked.
/// </summary>
[Collection(HostCompositionCollection.CollectionName)]
public sealed class ExternalActionHostBindingTests
{
    [Fact]
    public async Task ApiSharedComposition_AcceptsValidTelegramRouteWithoutWorkerCredentials()
    {
        using var host = CreateTelegramHost(
            includeWorker: false, new Dictionary<string, string?>());

        await host.StartAsync();

        using var scope = host.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IAgentToolRegistry>();
        Assert.True(registry.TryGet(TelegramNotificationToolDescriptor.ToolId, out var descriptor));
        Assert.Equal(TelegramNotificationToolDescriptor.LogicalTargetId, descriptor.LogicalTargetId);
        Assert.DoesNotContain(
            scope.ServiceProvider.GetRequiredService<PostReportActionWorkflowCatalog>().Workflows,
            workflow => workflow.ToolId == TelegramNotificationToolDescriptor.ToolId);
        Assert.DoesNotContain(
            scope.ServiceProvider.GetServices<IExternalActionTool>(),
            tool => tool.Definition.Name == TelegramNotificationToolDescriptor.ToolId);
        Assert.Null(host.Services.GetRequiredService<IConfiguration>()["IncidentCompass:Telegram:BotToken"]);
        Assert.Null(host.Services.GetRequiredService<IConfiguration>()["IncidentCompass:Telegram:ChatId"]);
        Assert.True(registry.TryGet(TicketCreateTool.ToolId, out var ticketDescriptor));
        Assert.Equal(TicketCreateTool.LogicalTargetId, ticketDescriptor.LogicalTargetId);
        Assert.DoesNotContain(
            scope.ServiceProvider.GetRequiredService<PostReportActionWorkflowCatalog>().Workflows,
            workflow => workflow.ToolId == TicketCreateTool.ToolId);
        Assert.DoesNotContain(
            scope.ServiceProvider.GetServices<IExternalActionTool>(),
            tool => tool.Definition.Name == TicketCreateTool.ToolId);
        Assert.True(registry.TryGet(
            TicketUpdatePostReportActionWorkflow.UpdateToolId, out var updateDescriptor));
        Assert.Equal(
            TicketUpdatePostReportActionWorkflow.UpdateLogicalTargetId,
            updateDescriptor.LogicalTargetId);
        Assert.DoesNotContain(
            scope.ServiceProvider.GetRequiredService<PostReportActionWorkflowCatalog>().Workflows,
            workflow => workflow.ToolId == TicketUpdatePostReportActionWorkflow.UpdateToolId);
        Assert.DoesNotContain(
            scope.ServiceProvider.GetServices<IExternalActionTool>(),
            tool => tool.Definition.Name == TicketUpdatePostReportActionWorkflow.UpdateToolId);
        Assert.Null(host.Services.GetRequiredService<IConfiguration>()["IncidentCompass:Tickets:GitHub:Token"]);
    }

    [Fact]
    public async Task WorkerTelegramBinding_RejectsMissingHostBindingWithoutExposingSecrets()
    {
        using var host = CreateTelegramHost(
            includeWorker: true, new Dictionary<string, string?>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartTelegramValidatorAsync(host.Services));

        AssertTelegramBindingFailureIsSecretFree(exception, Array.Empty<string>());
    }

    [Fact]
    public async Task WorkerTelegramBinding_RejectsMismatchedRouteWithoutExposingSecrets()
    {
        const string token = "123456:abcdefghijklmnopqrstuvwxyz";
        const string chatId = "-100987654321";
        using var host = CreateTelegramHost(includeWorker: true, new Dictionary<string, string?>
        {
            ["IncidentCompass:Telegram:Enabled"] = "true",
            ["IncidentCompass:Telegram:RouteId"] = "other_route",
            ["IncidentCompass:Telegram:ChatId"] = chatId,
            ["IncidentCompass:Telegram:BotToken"] = token
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartTelegramValidatorAsync(host.Services));

        AssertTelegramBindingFailureIsSecretFree(exception, [token, chatId, "api.telegram.org"]);
    }

    [Fact]
    public async Task WorkerTelegramBinding_AcceptsExactEnabledRouteBinding()
    {
        using var host = CreateTelegramHost(includeWorker: true, new Dictionary<string, string?>
        {
            ["IncidentCompass:Telegram:Enabled"] = "true",
            ["IncidentCompass:Telegram:RouteId"] = "telegram_ops",
            ["IncidentCompass:Telegram:ChatId"] = "-100987654321",
            ["IncidentCompass:Telegram:BotToken"] = "123456:abcdefghijklmnopqrstuvwxyz"
        });

        await StartTelegramValidatorAsync(host.Services);
    }

    [Fact]
    public async Task WorkerGitHubBinding_RejectsEnabledTicketCreateWithoutHostCredential()
    {
        using var host = CreateTicketHost(new Dictionary<string, string?>
        {
            ["IncidentCompass:Tickets:GitHub:Owner"] = "owner",
            ["IncidentCompass:Tickets:GitHub:Repository"] = "repo"
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartGitHubValidatorAsync(host.Services));

        Assert.Equal(
            "GitHub issue host binding does not match an enabled public ticket action.",
            exception.Message);
        Assert.DoesNotContain("owner/repo", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkerGitHubBinding_AcceptsExactEnabledTicketCreateBinding()
    {
        using var host = CreateTicketHost(new Dictionary<string, string?>
        {
            ["IncidentCompass:Tickets:GitHub:Owner"] = "owner",
            ["IncidentCompass:Tickets:GitHub:Repository"] = "repo",
            ["IncidentCompass:Tickets:GitHub:Token"] = "github-host-token-sentinel"
        });

        await StartGitHubValidatorAsync(host.Services);
    }

    [Fact]
    public async Task WorkerGitHubBinding_RejectsEnabledTicketUpdateWithoutHostCredential()
    {
        using var host = CreateTicketHost(new Dictionary<string, string?>
        {
            ["IncidentCompass:Tickets:GitHub:Owner"] = "owner",
            ["IncidentCompass:Tickets:GitHub:Repository"] = "repo"
        }, useUpdate: true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartGitHubValidatorAsync(host.Services));

        Assert.Equal(
            "GitHub issue host binding does not match an enabled public ticket action.",
            exception.Message);
        Assert.DoesNotContain("owner/repo", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkerGitHubBinding_AcceptsExactEnabledTicketUpdateBinding()
    {
        using var host = CreateTicketHost(new Dictionary<string, string?>
        {
            ["IncidentCompass:Tickets:GitHub:Owner"] = "owner",
            ["IncidentCompass:Tickets:GitHub:Repository"] = "repo",
            ["IncidentCompass:Tickets:GitHub:Token"] = "github-host-token-sentinel"
        }, useUpdate: true);

        await StartGitHubValidatorAsync(host.Services);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WorkerCodePublicationBinding_RejectsEnabledBranchPushWithoutABaseBranch(
        string? baseBranch)
    {
        // An unset base branch is what a host that never configured code publication looks like, and
        // such a host has to start. What it may not do is start while its configuration enables the
        // action, and that is this validator's whole job: the options validator lets blank through so
        // that a deployment which forwards an unset variable as an empty string comes up.
        using var host = CreateBranchPushHost(new Dictionary<string, string?>
        {
            ["IncidentCompass:Tickets:GitHub:Owner"] = "owner",
            ["IncidentCompass:Tickets:GitHub:Repository"] = "repo",
            ["IncidentCompass:Tickets:GitHub:Token"] = "github-host-token-sentinel",
            ["IncidentCompass:Publication:GitHub:BaseBranch"] = baseBranch
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartCodePublicationValidatorAsync(host.Services));

        Assert.Equal(
            "GitHub code publication binding does not match an enabled branch push action.",
            exception.Message);
        Assert.DoesNotContain("owner/repo", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "github-host-token-sentinel", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkerCodePublicationBinding_RejectsEnabledBranchPushWithoutAHostCredential()
    {
        using var host = CreateBranchPushHost(new Dictionary<string, string?>
        {
            ["IncidentCompass:Tickets:GitHub:Owner"] = "owner",
            ["IncidentCompass:Tickets:GitHub:Repository"] = "repo",
            ["IncidentCompass:Publication:GitHub:BaseBranch"] = "main"
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartCodePublicationValidatorAsync(host.Services));

        Assert.Equal(
            "GitHub code publication binding does not match an enabled branch push action.",
            exception.Message);
    }

    [Fact]
    public async Task WorkerCodePublicationBinding_AcceptsExactEnabledBranchPushBinding()
    {
        using var host = CreateBranchPushHost(new Dictionary<string, string?>
        {
            ["IncidentCompass:Tickets:GitHub:Owner"] = "owner",
            ["IncidentCompass:Tickets:GitHub:Repository"] = "repo",
            ["IncidentCompass:Tickets:GitHub:Token"] = "github-host-token-sentinel",
            ["IncidentCompass:Publication:GitHub:BaseBranch"] = "main"
        });

        await StartCodePublicationValidatorAsync(host.Services);
    }

    private static IHost CreateTelegramHost(
        bool includeWorker,
        IReadOnlyDictionary<string, string?> telegramValues)
    {
        var values = new Dictionary<string, string?>(telegramValues)
        {
            ["IncidentCompass:ModelGateway:Provider"] = "Mock",
            ["IncidentCompass:Embeddings:Provider"] = "Mock"
        };
        return new HostBuilder()
            .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(values))
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddTestApplication(context.Configuration);
                services.AddInfrastructure(context.Configuration);
                services.RemoveAll<ITriageConfigurationRepository>();
                services.AddSingleton<ITriageConfigurationRepository>(
                    new StaticNotificationConfiguration(CreateTelegramConfiguration()));
                if (includeWorker)
                {
                    services.AddWorker(context.Configuration);
                }
            })
            .Build();
    }

    private static TriageConfiguration CreateTelegramConfiguration() => new(
        "telegram-host-composition",
        new Dictionary<string, TriageProviderSettings>(StringComparer.Ordinal),
        new Dictionary<string, TriageRouteSettings>(StringComparer.Ordinal),
        new OrchestratorSettings("orchestrator", "chat", ["delegate", "publish_report"],
            new OrchestratorBudgetSettings(1, 1000, 30)),
        new Dictionary<string, TriageRoleSettings>(StringComparer.Ordinal),
        new Dictionary<string, TriageToolSettings>(StringComparer.Ordinal)
        {
            [TelegramNotificationToolDescriptor.ToolId] = new(
                "external_action", null, null, null, "notification",
                TelegramNotificationToolDescriptor.LogicalTargetId)
        },
        [],
        new IngestionSettings("tenant", ["tester"]),
        new FaultGroupingSettings(15, 30, 1, new MassIssueSettings(5, "strong")),
        RedactionSettings.Default)
    {
        Actions = new TriageActionSettings(
            [TelegramNotificationToolDescriptor.ToolId], "live", false, 60)
        {
            NotificationRoutes =
            [
                new NotificationRoute(
                    "telegram_ops", TelegramNotificationToolDescriptor.ToolId,
                    null, null, ["error"])
            ]
        }
    };

    private static IHost CreateTicketHost(
        IReadOnlyDictionary<string, string?> ticketValues,
        bool useUpdate = false)
    {
        var values = new Dictionary<string, string?>(ticketValues)
        {
            ["IncidentCompass:ModelGateway:Provider"] = "Mock",
            ["IncidentCompass:Embeddings:Provider"] = "Mock"
        };
        return new HostBuilder()
            .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(values))
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddTestApplication(context.Configuration);
                services.AddInfrastructure(context.Configuration);
                services.RemoveAll<ITriageConfigurationRepository>();
                services.AddSingleton<ITriageConfigurationRepository>(
                    new StaticNotificationConfiguration(CreateTicketConfiguration(useUpdate)));
                services.AddWorker(context.Configuration);
            })
            .Build();
    }

    private static TriageConfiguration CreateTicketConfiguration(bool useUpdate)
    {
        var toolId = useUpdate
            ? TicketUpdatePostReportActionWorkflow.UpdateToolId
            : TicketCreateTool.ToolId;
        var category = useUpdate ? "ticket_update" : "ticket_create";
        var logicalTargetId = useUpdate
            ? TicketUpdatePostReportActionWorkflow.UpdateLogicalTargetId
            : TicketCreateTool.LogicalTargetId;
        return new(
            "ticket-host-composition",
            new Dictionary<string, TriageProviderSettings>(StringComparer.Ordinal),
            new Dictionary<string, TriageRouteSettings>(StringComparer.Ordinal),
            new OrchestratorSettings("orchestrator", "chat", ["delegate", "publish_report"],
                new OrchestratorBudgetSettings(1, 1000, 30)),
            new Dictionary<string, TriageRoleSettings>(StringComparer.Ordinal),
            new Dictionary<string, TriageToolSettings>(StringComparer.Ordinal)
            {
                [toolId] = new(
                    "external_action", null, null, null, category, logicalTargetId)
            },
            [],
            new IngestionSettings("tenant", ["tester"]),
            new FaultGroupingSettings(15, 30, 1, new MassIssueSettings(5, "strong")),
            RedactionSettings.Default)
        {
            Actions = new TriageActionSettings([toolId], "live", false, 60)
        };
    }

    private static Task StartTelegramValidatorAsync(IServiceProvider services)
    {
        var validator = services.GetServices<IHostedService>().Single(service =>
            service.GetType().FullName == "IncidentCompass.Worker.TelegramConfigurationStartupValidator");
        return validator.StartAsync(TestContext.Current.CancellationToken);
    }

    private static Task StartGitHubValidatorAsync(IServiceProvider services)
    {
        var validator = services.GetServices<IHostedService>().Single(service =>
            service.GetType().FullName == "IncidentCompass.Worker.GitHubIssueConfigurationStartupValidator");
        return validator.StartAsync(TestContext.Current.CancellationToken);
    }

    private static Task StartCodePublicationValidatorAsync(IServiceProvider services)
    {
        var validator = services.GetServices<IHostedService>().Single(service =>
            service.GetType().FullName ==
            "IncidentCompass.Worker.CodePublicationConfigurationStartupValidator");
        return validator.StartAsync(TestContext.Current.CancellationToken);
    }

    private static IHost CreateBranchPushHost(IReadOnlyDictionary<string, string?> publicationValues)
    {
        var values = new Dictionary<string, string?>(publicationValues)
        {
            ["IncidentCompass:ModelGateway:Provider"] = "Mock",
            ["IncidentCompass:Embeddings:Provider"] = "Mock"
        };
        return new HostBuilder()
            .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(values))
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddTestApplication(context.Configuration);
                services.AddInfrastructure(context.Configuration);
                services.RemoveAll<ITriageConfigurationRepository>();
                services.AddSingleton<ITriageConfigurationRepository>(
                    new StaticNotificationConfiguration(CreateBranchPushConfiguration()));
                services.AddWorker(context.Configuration);
            })
            .Build();
    }

    private static TriageConfiguration CreateBranchPushConfiguration() => new(
        "branch-push-host-composition",
        new Dictionary<string, TriageProviderSettings>(StringComparer.Ordinal),
        new Dictionary<string, TriageRouteSettings>(StringComparer.Ordinal),
        new OrchestratorSettings("orchestrator", "chat", ["delegate", "publish_report"],
            new OrchestratorBudgetSettings(1, 1000, 30)),
        new Dictionary<string, TriageRoleSettings>(StringComparer.Ordinal),
        new Dictionary<string, TriageToolSettings>(StringComparer.Ordinal)
        {
            [BranchPushToolDescriptor.ToolId] = new(
                "external_action", null, null, null, "branch_push",
                BranchPushToolDescriptor.LogicalTargetId)
        },
        [],
        new IngestionSettings("tenant", ["tester"]),
        new FaultGroupingSettings(15, 30, 1, new MassIssueSettings(5, "strong")),
        RedactionSettings.Default)
    {
        Actions = new TriageActionSettings([BranchPushToolDescriptor.ToolId], "live", false, 60)
    };

    private static void AssertTelegramBindingFailureIsSecretFree(
        InvalidOperationException exception,
        IReadOnlyCollection<string> sentinels)
    {
        Assert.Equal(
            "Telegram host binding does not match the enabled public notification route.",
            exception.Message);
        foreach (var sentinel in sentinels)
        {
            Assert.DoesNotContain(sentinel, exception.ToString(), StringComparison.Ordinal);
        }
    }
}
