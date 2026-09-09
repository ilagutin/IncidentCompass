using System.Text.Json.Nodes;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Normalization;
using IncidentCompass.Application.Notifications;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.Intake;
using IncidentCompass.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
namespace IncidentCompass.UnitTests;

[Collection(TriageConfigurationValidationParityTests.ConsoleCollectionName)]
public sealed class TriageConfigurationValidationParityTests
{
    public const string ConsoleCollectionName = "Triage configuration console output";
    private static readonly SemaphoreSlim ConsoleLock = new(1, 1);

    public static TheoryData<string, bool, bool, string?> FixtureCases => new()
    {
        { "valid", true, true, null },
        { "provider", false, false, "Providers.local-oai.Kind" },
        { "route", false, false, "Routes.analysis-chat.Model" },
        { "valid-reasoning", true, true, null },
        { "unknown-reasoning", false, false, "Routes.analysis-chat.Reasoning" },
        { "numeric-reasoning", false, false, "Routes.analysis-chat.Reasoning" },
        { "embedding-reasoning", false, false, "Routes.memory-embed.Reasoning" },
        { "role", false, false, "Roles.analysis.OutputSchema" },
        { "tool", false, false, "Tools.memory_search.Kind" },
        { "rule", false, false, "Rules.Type" },
        { "budget", false, false, "Orchestrator.Budget.MaxTokens" },
        { "ingestion", false, false, "Ingestion.AllowedSources" },
        { "grouping", false, false, "FaultGrouping.LookbackMinutes" },
        { "recurrence", false, false, "FaultGrouping.Recurrence.EscalateAfterCount" },
        { "redaction", false, false, "Redaction.Patterns[0].Name" },
        { "dangling-role-route", true, false, "Roles.analysis.RouteId" },
        { "valid-external-action", true, true, null },
        { "valid-telegram-action", true, true, null },
        { "valid-ticket-create-action", true, true, null },
        { "valid-mixed-case-external-action", true, true, null },
        { "unsafe-external-action-id", false, false, "Tools.notify:test" },
        { "external-action-in-role", true, false, "Roles.analysis.Tools" },
        { "immediate-read-in-actions", true, false, "Actions.AllowedTools" },
        { "duplicate-action-grant", false, false, "Actions.AllowedTools" },
        { "unknown-action", true, false, "Actions.AllowedTools" },
        { "unregistered-external-action", true, false, "Tools.unknown_external" },
        { "action-category-mismatch", true, false, "Tools.notify_test.Category" },
        { "action-target-mismatch", true, false, "Tools.notify_test.LogicalTargetId" },
        { "read-reclassified-as-action", true, false, "Tools.memory_search.Kind" },
        { "action-mode-loosens-global", true, false, "Tools.notify_test.Mode" },
        { "duplicate-notification-route", true, false, "Actions.NotificationRoutes.ToolId" },
        { "notification-selector-not-normalized", false, false, "Actions.NotificationRoutes.ServiceName" },
        { "notification-empty-severity", false, false, "Actions.NotificationRoutes.Severities" },
        { "notification-null-routes", false, false, "Actions.NotificationRoutes" },
        { "notification-null-severities", false, false, "Actions.NotificationRoutes.Severities" },
        { "wildcard-approval", true, false, "Rules.requires_approval.Tool" },
        { "read-tool-approval", true, false, "Rules.requires_approval.Tool" }
    };

    [Theory]
    [MemberData(nameof(FixtureCases))]
    public async Task Schema_Command_AndStartup_UseTheExpectedValidationBoundary(
        string fixtureName,
        bool schemaIsValid,
        bool runtimeIsValid,
        string? expectedSection)
    {
        using var fixture = TemporaryConfigFixture.Create(fixtureName);
        var configNode = fixture.LoadConfig();
        ApplyFixture(fixtureName, configNode);
        fixture.SaveConfig(configNode);

        var schemaResult = TriageConfigSchemaTests.EvaluateFixture(configNode);
        Assert.Equal(schemaIsValid, schemaResult.IsValid);

        var snapshots = new RecordingSnapshotStore();
        var hostProbe = new HostedServiceProbe();
        await using var services = CreateServices(fixture.RootPath, snapshots, hostProbe);
        var repository = services.GetRequiredService<FileTriageConfigurationRepository>();
        var warmup = new TriageConfigurationWarmupHostedService(repository);
        string? startupMessage = null;

        if (runtimeIsValid)
        {
            await warmup.StartAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, snapshots.PersistCallCount);
        }
        else
        {
            var startupException = await Assert.ThrowsAsync<InvalidOperationException>(
                () => warmup.StartAsync(TestContext.Current.CancellationToken));
            startupMessage = startupException.Message;
            Assert.Contains(expectedSection!, startupMessage, StringComparison.Ordinal);
            Assert.Equal(0, snapshots.PersistCallCount);
        }

        var command = await RunCommandAsync(services);

        Assert.Equal(runtimeIsValid ? 0 : 1, command.ExitCode);
        Assert.False(hostProbe.StartCalled);
        if (runtimeIsValid)
        {
            Assert.Contains(fixture.ConfigPath, command.StandardOutput, StringComparison.Ordinal);
            Assert.Empty(command.StandardError);
            Assert.Equal(1, snapshots.PersistCallCount);
        }
        else
        {
            Assert.Contains(fixture.ConfigPath, command.StandardError, StringComparison.Ordinal);
            Assert.Contains(expectedSection!, command.StandardError, StringComparison.Ordinal);
            Assert.Contains(startupMessage!, command.StandardError, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ConfigValidate_DoesNotStartAHostOrPersistASnapshot()
    {
        using var fixture = TemporaryConfigFixture.Create("valid");
        var snapshots = new RecordingSnapshotStore();
        var hostProbe = new HostedServiceProbe();
        await using var services = CreateServices(fixture.RootPath, snapshots, hostProbe);

        var command = await RunCommandAsync(services);

        Assert.Equal(0, command.ExitCode);
        Assert.Contains(fixture.ConfigPath, command.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(command.StandardError);
        Assert.False(hostProbe.StartCalled);
        Assert.Equal(0, snapshots.PersistCallCount);
    }

    private static void ApplyFixture(string fixtureName, JsonObject root)
    {
        switch (fixtureName)
        {
            case "valid":
                return;
            case "provider":
                root["Providers"]!["local-oai"]!["Kind"] = "Unsupported";
                return;
            case "route":
                root["Routes"]!["analysis-chat"]!["Model"] = string.Empty;
                return;
            case "valid-reasoning":
                root["Routes"]!["analysis-chat"]!["Reasoning"] = "low";
                return;
            case "unknown-reasoning":
                root["Routes"]!["analysis-chat"]!["Reasoning"] = "automatic";
                return;
            case "numeric-reasoning":
                root["Routes"]!["analysis-chat"]!["Reasoning"] = 1;
                return;
            case "embedding-reasoning":
                root["Routes"]!["memory-embed"]!["Reasoning"] = "off";
                return;
            case "role":
                root["Roles"]!["analysis"]!["OutputSchema"] = string.Empty;
                return;
            case "tool":
                root["Tools"]!["memory_search"]!["Kind"] = "external";
                return;
            case "rule":
                root["Rules"]![0]!["Type"] = "unknown";
                return;
            case "budget":
                root["Orchestrator"]!["Budget"]!["MaxTokens"] = 0;
                return;
            case "ingestion":
                root["Ingestion"]!["AllowedSources"] = new JsonArray("webhook");
                return;
            case "grouping":
                root["FaultGrouping"]!["LookbackMinutes"] = 0;
                return;
            case "recurrence":
                root["FaultGrouping"]!["Recurrence"]!["EscalateAfterCount"] = 0;
                return;
            case "redaction":
                root["Redaction"]!["Patterns"] = new JsonArray(new JsonObject
                {
                    ["Name"] = string.Empty,
                    ["Pattern"] = "secret"
                });
                return;
            case "dangling-role-route":
                root["Roles"]!["analysis"]!["RouteId"] = "missing-route";
                return;
            case "valid-external-action":
                AddExternalAction(root);
                return;
            case "valid-telegram-action":
                AddExternalAction(
                    root,
                    TelegramNotificationToolDescriptor.ToolId,
                    TelegramNotificationToolDescriptor.LogicalTargetId,
                    "telegram_ops");
                return;
            case "valid-ticket-create-action":
                root["Tools"]![TicketCreateTool.ToolId]!["Mode"] = "live";
                root["Actions"]!["AllowedTools"] = new JsonArray(TicketCreateTool.ToolId);
                root["Actions"]!["DefaultMode"] = "live";
                return;
            case "valid-mixed-case-external-action":
                AddExternalAction(root, "Action_Test.v1-Edge");
                return;
            case "unsafe-external-action-id":
                AddExternalAction(root, "notify:test");
                return;
            case "external-action-in-role":
                AddExternalAction(root);
                root["Roles"]!["analysis"]!["Tools"]!.AsArray().Add("notify_test");
                return;
            case "immediate-read-in-actions":
                root["Actions"]!["AllowedTools"] = new JsonArray("memory_search");
                return;
            case "duplicate-action-grant":
                AddExternalAction(root);
                root["Actions"]!["AllowedTools"] = new JsonArray("notify_test", "notify_test");
                return;
            case "unknown-action":
                root["Actions"]!["AllowedTools"] = new JsonArray("missing_action");
                return;
            case "unregistered-external-action":
                root["Tools"]!["unknown_external"] = new JsonObject
                {
                    ["Kind"] = "external_action",
                    ["Category"] = "notification",
                    ["LogicalTargetId"] = "telegram:ops"
                };
                root["Actions"]!["AllowedTools"] = new JsonArray("unknown_external");
                return;
            case "action-category-mismatch":
                AddExternalAction(root);
                root["Tools"]!["notify_test"]!["Category"] = "ticket_create";
                return;
            case "action-target-mismatch":
                AddExternalAction(root);
                root["Tools"]!["notify_test"]!["LogicalTargetId"] = "telegram:other";
                return;
            case "read-reclassified-as-action":
                root["Tools"]!["memory_search"]!["Kind"] = "external_action";
                root["Tools"]!["memory_search"]!["Category"] = "notification";
                root["Tools"]!["memory_search"]!["LogicalTargetId"] = "telegram:ops";
                root["Roles"]!["memory"]!["Tools"] = new JsonArray();
                root["Actions"]!["AllowedTools"] = new JsonArray("memory_search");
                return;
            case "action-mode-loosens-global":
                AddExternalAction(root);
                root["Actions"]!["DefaultMode"] = "dry_run";
                root["Tools"]!["notify_test"]!["Mode"] = "live";
                return;
            case "duplicate-notification-route":
                AddExternalAction(root);
                root["Actions"]!["NotificationRoutes"]!.AsArray().Add(new JsonObject
                {
                    ["RouteId"] = "telegram_backup",
                    ["ToolId"] = "notify_test",
                    ["Severities"] = new JsonArray("critical")
                });
                return;
            case "notification-selector-not-normalized":
                AddExternalAction(root);
                root["Actions"]!["NotificationRoutes"]![0]!["ServiceName"] = "Checkout";
                return;
            case "notification-empty-severity":
                AddExternalAction(root);
                root["Actions"]!["NotificationRoutes"]![0]!["Severities"] = new JsonArray();
                return;
            case "notification-null-routes":
                root["Actions"]!["NotificationRoutes"] = null;
                return;
            case "notification-null-severities":
                AddExternalAction(root);
                root["Actions"]!["NotificationRoutes"]![0]!["Severities"] = null;
                return;
            case "wildcard-approval":
                AddExternalAction(root);
                root["Rules"]!.AsArray().Add(new JsonObject
                {
                    ["Type"] = "requires_approval",
                    ["Tool"] = "*",
                    ["Scope"] = "attempt"
                });
                return;
            case "read-tool-approval":
                root["Rules"]!.AsArray().Add(new JsonObject
                {
                    ["Type"] = "requires_approval",
                    ["Tool"] = "memory_search",
                    ["Scope"] = "attempt"
                });
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(fixtureName), fixtureName, "Unknown fixture.");
        }
    }

    private static void AddExternalAction(
        JsonObject root,
        string toolId = "notify_test",
        string logicalTargetId = "telegram:ops",
        string routeId = "telegram_ops")
    {
        root["Tools"]![toolId] = new JsonObject
        {
            ["Kind"] = "external_action",
            ["Category"] = "notification",
            ["LogicalTargetId"] = logicalTargetId
        };
        root["Actions"]!["AllowedTools"] = new JsonArray(toolId);
        root["Actions"]!["DefaultMode"] = "live";
        root["Actions"]!["NotificationRoutes"] = new JsonArray
        {
            new JsonObject
            {
                ["RouteId"] = routeId,
                ["ToolId"] = toolId,
                ["ServiceName"] = "checkout",
                ["Environment"] = "production",
                ["Severities"] = new JsonArray("error", "critical", "fatal")
            }
        };
    }

    private static ServiceProvider CreateServices(
        string contentRootPath,
        RecordingSnapshotStore snapshots,
        HostedServiceProbe hostProbe)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(contentRootPath));
        services.AddSingleton<IOptions<TriageConfigSourceOptions>>(Options.Create(new TriageConfigSourceOptions
        {
            Kind = "File",
            Path = Path.Combine("config", "incidentcompass.config.json")
        }));
        services.AddSingleton(new SignalNormalizerRegistry([
            new TesterSignalNormalizer(),
            new OtelShapedSignalNormalizer(),
            new UserReportSignalNormalizer()
        ]));
        services.AddSingleton<IAgentToolRegistry>(new AgentToolRegistry([
            new AgentToolDescriptor("memory_search", AgentToolCapability.ImmediateRead),
            new AgentToolDescriptor("source_lookup", AgentToolCapability.ImmediateRead),
            new AgentToolDescriptor("ticket_search", AgentToolCapability.ImmediateRead),
            TelegramNotificationToolDescriptor.Value,
            TicketCreateTool.Descriptor,
            new AgentToolDescriptor(
                "notify_test",
                AgentToolCapability.ExternalAction,
                IncidentCompass.Domain.Incidents.Actions.ActionCategory.Notification,
                "telegram:ops"),
            new AgentToolDescriptor(
                "Action_Test.v1-Edge",
                AgentToolCapability.ExternalAction,
                IncidentCompass.Domain.Incidents.Actions.ActionCategory.Notification,
                "telegram:ops")
        ]));
        services.AddSingleton<TriageConfigurationLoadValidator>();
        services.AddSingleton<TriageConfigurationMaterializer>();
        services.AddSingleton<ITriageConfigurationSnapshotStore>(snapshots);
        services.AddSingleton<FileTriageConfigurationRepository>();
        services.AddSingleton<IHostedService>(hostProbe);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task<CommandResult> RunCommandAsync(IServiceProvider services)
    {
        await ConsoleLock.WaitAsync(TestContext.Current.CancellationToken);
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await TriageConfigurationValidationCommand.RunIfRequestedAsync(
                ["config", "validate"],
                services,
                TestContext.Current.CancellationToken);
            return new CommandResult(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            ConsoleLock.Release();
        }
    }

    private sealed record CommandResult(int? ExitCode, string StandardOutput, string StandardError);

    private sealed class RecordingSnapshotStore : ITriageConfigurationSnapshotStore
    {
        public int PersistCallCount { get; private set; }

        public Task PersistAsync(
            string configHash,
            JsonNode configNode,
            JsonObject instructionsNode,
            CancellationToken cancellationToken)
        {
            PersistCallCount++;
            return Task.CompletedTask;
        }

        public Task<TriageConfigurationSnapshotDocument?> GetAsync(
            string configHash,
            CancellationToken cancellationToken) =>
            Task.FromResult<TriageConfigurationSnapshotDocument?>(null);
    }

    private sealed class HostedServiceProbe : IHostedService
    {
        public bool StartCalled { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCalled = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "IncidentCompass.UnitTests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TemporaryConfigFixture : IDisposable
    {
        private TemporaryConfigFixture(string rootPath)
        {
            RootPath = rootPath;
            ConfigPath = Path.Combine(RootPath, "config", "incidentcompass.config.json");
        }

        public string RootPath { get; }
        public string ConfigPath { get; }

        public static TemporaryConfigFixture Create(string fixtureName)
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "IncidentCompass", "config-validation", fixtureName, Guid.NewGuid().ToString("N"));
            var sourcePath = Path.Combine(RepositoryRootLocator.Find(), "config");
            foreach (var sourceFile in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourcePath, sourceFile);
                var destinationPath = Path.Combine(rootPath, "config", relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Copy(sourceFile, destinationPath);
            }

            return new TemporaryConfigFixture(rootPath);
        }

        public JsonObject LoadConfig() => JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();

        public void SaveConfig(JsonObject config) => File.WriteAllText(ConfigPath, config.ToJsonString());

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}

[CollectionDefinition(TriageConfigurationValidationParityTests.ConsoleCollectionName, DisableParallelization = true)]
public sealed class TriageConfigurationConsoleCollection;
