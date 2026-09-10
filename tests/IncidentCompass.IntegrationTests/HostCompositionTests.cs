using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.Security;
using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Notifications;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Infrastructure;
using IncidentCompass.Infrastructure.Tickets;
using IncidentCompass.TestSupport;
using IncidentCompass.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WorkerService = IncidentCompass.Worker.Worker;

namespace IncidentCompass.IntegrationTests;

public sealed class HostCompositionTests
{
    public static IEnumerable<object[]> InvalidApplicationConfigurations =>
    [
        [new Dictionary<string, string?> { ["IncidentCompass:Application:ApiVersion"] = " " }]
    ];

    public static IEnumerable<object[]> InvalidModelGatewayConfigurations =>
    [
        [new Dictionary<string, string?> { ["IncidentCompass:ModelGateway:Provider"] = " " }],
        [new Dictionary<string, string?> { ["IncidentCompass:ModelGateway:DefaultTemperature"] = "1.5" }],
        [
            new Dictionary<string, string?>
            {
                ["IncidentCompass:ModelGateway:MinTemperature"] = "0.8",
                ["IncidentCompass:ModelGateway:MaxTemperature"] = "0.7"
            }
        ],
        [new Dictionary<string, string?> { ["IncidentCompass:ModelGateway:DefaultMaxOutputTokens"] = "0" }],
        [
            new Dictionary<string, string?>
            {
                ["IncidentCompass:ModelGateway:DefaultMaxOutputTokens"] = "4096",
                ["IncidentCompass:ModelGateway:MaxOutputTokensLimit"] = "2048"
            }
        ],
        [new Dictionary<string, string?> { ["IncidentCompass:ModelGateway:MaxInputMessageCharacters"] = "0" }],
        [new Dictionary<string, string?> { ["IncidentCompass:ModelGateway:MaxCorrelationIdLength"] = "129" }]
    ];

    public static IEnumerable<object[]> AcceptedProviderSpellings =>
    [
        ["Mock"],
        ["OpenAiCompatible"],
        ["OPENAI_COMPATIBLE"],
        ["OPENAI-COMPATIBLE"]
    ];

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

        // Investigation/action workers + Infrastructure warmups for config and optional memory seeding.
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        Assert.Equal(7, hostedServices.Length);
        Assert.Contains(hostedServices, service =>
            service.GetType().FullName == "IncidentCompass.Worker.TelegramConfigurationStartupValidator");
        Assert.Contains(hostedServices, service =>
            service.GetType().FullName == "IncidentCompass.Worker.GitHubIssueConfigurationStartupValidator");
        Assert.Contains(hostedServices, service => service is WorkerService);
        Assert.Contains(hostedServices, service =>
            service.GetType().FullName == "IncidentCompass.Worker.ActionDispatchWorker");
        Assert.Contains(hostedServices, service =>
            service.GetType().FullName == "IncidentCompass.Worker.PostReportActionEvaluationWorker");
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

    [Fact]
    public async Task HostServices_RejectUnsupportedModelGatewayProviderOnStart()
    {
        using var host = new HostBuilder()
            .ConfigureAppConfiguration(configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["IncidentCompass:ModelGateway:Provider"] = "TypoProvider"
                });
            })
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddTestApplication(context.Configuration);
                services.AddInfrastructure(context.Configuration);
            })
            .Build();

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        Assert.NotNull(exception);
        Assert.Contains(
            GetOptionsValidationFailures(exception),
            failure => failure.Contains("unsupported", StringComparison.OrdinalIgnoreCase) &&
                       failure.Contains("TypoProvider", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(AcceptedProviderSpellings))]
    public async Task HostServices_AcceptsDocumentedProviderSpellings(string provider)
    {
        using var host = CreateHostWithConfiguration(new Dictionary<string, string?>
        {
            ["IncidentCompass:ModelGateway:Provider"] = provider,
            ["IncidentCompass:ModelGateway:OpenAiCompatible:ApiKey"] = "test-api-key",
            ["IncidentCompass:Embeddings:Provider"] = provider,
            ["IncidentCompass:Embeddings:OpenAiCompatible:ApiKey"] = "test-api-key"
        });

        await host.StartAsync();

        using var scope = host.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAiModelClient>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IEmbeddingClient>());
    }

    [Theory]
    [MemberData(nameof(InvalidApplicationConfigurations))]
    public async Task HostServices_RejectInvalidApplicationOptionsOnStart(
        IReadOnlyDictionary<string, string?> invalidConfiguration)
    {
        using var host = CreateHostWithConfiguration(invalidConfiguration);

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        // [OptionsValidator] (source-generated) emits one failure per failing property, formatted
        // by the attribute's ErrorMessage. The invalidated field name appears in the failure
        // text, which is what we anchor the assertion on now.
        var expectedFieldName = invalidConfiguration.Keys.First().Split(':')[^1];
        Assert.NotNull(exception);
        Assert.Contains(
            GetOptionsValidationFailures(exception),
            failure => failure.Contains(expectedFieldName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HostServices_RejectInvalidEmbeddingOptionsOnStart()
    {
        using var host = CreateHostWithConfiguration(new Dictionary<string, string?>
        {
            ["IncidentCompass:Embeddings:MockDimensions"] = "0"
        });

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        // [OptionsValidator] (source-generated) emits one failure per failing property; the
        // invalidated field name appears in the failure text.
        Assert.NotNull(exception);
        Assert.Contains(
            GetOptionsValidationFailures(exception),
            failure => failure.Contains("MockDimensions", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(InvalidModelGatewayConfigurations))]
    public async Task HostServices_RejectInvalidModelGatewayOptionsOnStart(
        IReadOnlyDictionary<string, string?> values)
    {
        using var host = CreateHostWithConfiguration(values);

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        Assert.NotNull(exception);
        Assert.Contains(
            GetOptionsValidationFailures(exception),
            failure => failure.Contains("Model gateway configuration", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("1023", "1")]
    [InlineData("1048577", "1")]
    [InlineData("1024", "0")]
    [InlineData("1024", "1025")]
    public async Task HostServices_RejectInvalidIngestionLimitsOnStart(string payloadBytes, string attributesBytes)
    {
        using var host = CreateHostWithConfiguration(new Dictionary<string, string?>
        {
            ["IncidentCompass:IngestionLimits:MaxPayloadBytes"] = payloadBytes,
            ["IncidentCompass:IngestionLimits:MaxAttributesBytes"] = attributesBytes
        });

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        Assert.NotNull(exception);
        Assert.NotEmpty(GetOptionsValidationFailures(exception));
    }

    [Theory]
    [InlineData("1024", "1")]
    [InlineData("1048576", "1048576")]
    public async Task HostServices_AcceptsIngestionLimitsAtInclusiveBounds(string payloadBytes, string attributesBytes)
    {
        using var host = CreateHostWithConfiguration(new Dictionary<string, string?>
        {
            ["IncidentCompass:IngestionLimits:MaxPayloadBytes"] = payloadBytes,
            ["IncidentCompass:IngestionLimits:MaxAttributesBytes"] = attributesBytes
        });

        await host.StartAsync();
    }
    [Fact]
    public async Task MockEmbeddingClient_UsesConfiguredDimensions()
    {
        using var host = CreateHostWithConfiguration(new Dictionary<string, string?>
        {
            ["IncidentCompass:Embeddings:MockDimensions"] = "1024"
        });
        await host.StartAsync();

        var embeddingClient = host.Services.GetRequiredService<IEmbeddingClient>();
        var response = await embeddingClient.CreateEmbeddingAsync(
            new EmbeddingRequest("dimension test", "mock-embedding", CorrelationId: null),
            TestContext.Current.CancellationToken);

        Assert.Equal(1024, response.Vector.Count);
    }

    [Fact]
    public async Task HostServices_ValidateAndComposeGitHubTicketSearchWithoutARealCall()
    {
        using var host = CreateHostWithConfiguration(new Dictionary<string, string?>
        {
            ["IncidentCompass:Tickets:GitHub:Owner"] = "owner",
            ["IncidentCompass:Tickets:GitHub:Repository"] = "repo",
            ["IncidentCompass:Tickets:GitHub:Token"] = "host-secret"
        });

        await host.StartAsync();
        using var scope = host.Services.CreateScope();

        Assert.IsType<GitHubIssuesTicketSearch>(scope.ServiceProvider.GetRequiredService<ITicketSearch>());
    }

    [Fact]
    public async Task HostServices_RejectInvalidGitHubRepositoryIdentityOnStart()
    {
        using var host = CreateHostWithConfiguration(new Dictionary<string, string?>
        {
            ["IncidentCompass:Tickets:GitHub:Owner"] = "owner",
            ["IncidentCompass:Tickets:GitHub:Repository"] = "other/repo"
        });

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        Assert.NotNull(exception);
        Assert.Contains(GetOptionsValidationFailures(exception),
            failure => failure.Contains("Repository", StringComparison.Ordinal));
    }

    private static IHost CreateHostWithConfiguration(
        IReadOnlyDictionary<string, string?> values)
    {
        // These hosts exercise ModelGateway/Embeddings option validation only, but AddInfrastructure
        // now also registers the intake infrastructure, whose warmup hosted service needs a
        // real triage config file to resolve at StartAsync -- point it at the repo's checked-in
        // config so these unrelated tests do not need to know about intake at all.
        var configurationOverrides = new Dictionary<string, string?>(values)
        {
            ["IncidentCompass:ConfigSource:Path"] = Path.Combine(RepositoryRootLocator.Find(), "config", "incidentcompass.config.json")
        };

        return new HostBuilder()
            .ConfigureAppConfiguration(configuration =>
            {
                configuration.AddInMemoryCollection(configurationOverrides);
            })
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddTestApplication(context.Configuration);
                services.AddInfrastructure(context.Configuration);
            })
            .Build();
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

    private static IEnumerable<string> GetOptionsValidationFailures(Exception exception)
    {
        if (exception is OptionsValidationException optionsValidationException)
        {
            return optionsValidationException.Failures;
        }

        if (exception is AggregateException aggregateException)
        {
            return aggregateException
                .Flatten()
                .InnerExceptions
                .OfType<OptionsValidationException>()
                .SelectMany(static optionsValidationException => optionsValidationException.Failures);
        }

        return [];
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
