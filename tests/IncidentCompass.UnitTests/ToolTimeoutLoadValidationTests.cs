using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Intake.Normalization;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.Intake;
using IncidentCompass.TestSupport;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

/// <summary>
/// <c>Tools.&lt;id&gt;.TimeoutSeconds</c> is optional and allowed on both tool kinds. The loader and
/// the published schema accept the same range, and a configuration file that does not set it is
/// hashed and snapshotted from exactly its own content, so its configuration hash does not move.
/// </summary>
public sealed class ToolTimeoutLoadValidationTests
{
    private const string ReadTool = "memory_search";
    private const string ActionTool = TicketCreateTool.ToolId;

    [Theory]
    [InlineData(ReadTool, 1)]
    [InlineData(ReadTool, 3600)]
    [InlineData(ActionTool, 1)]
    [InlineData(ActionTool, 3600)]
    public void Materialize_TimeoutInRange_IsAcceptedOnBothKinds(string toolName, int seconds)
    {
        var node = ConfigNode();
        Tool(node, toolName)["TimeoutSeconds"] = seconds;

        var configuration = Materialize(node);

        Assert.Equal(seconds, configuration.Tools[toolName].TimeoutSeconds);
    }

    [Theory]
    [InlineData(ReadTool, 0)]
    [InlineData(ReadTool, 3601)]
    [InlineData(ActionTool, 0)]
    [InlineData(ActionTool, -5)]
    [InlineData(ActionTool, 3601)]
    public void Materialize_TimeoutOutOfRange_FailsNamingTheKey(string toolName, int seconds)
    {
        var node = ConfigNode();
        Tool(node, toolName)["TimeoutSeconds"] = seconds;

        var exception = Assert.Throws<TriageConfigurationLoadException>(() => Materialize(node));

        Assert.Contains("Tools." + toolName + ".TimeoutSeconds", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_TimeoutAbsent_ResolvesTheDocumentedDefaults()
    {
        var configuration = Materialize(ConfigNode());

        Assert.Null(configuration.Tools[ReadTool].TimeoutSeconds);
        Assert.Equal(TimeSpan.FromSeconds(120), configuration.Tools[ReadTool].ResolveImmediateTimeout());
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            configuration.Tools[ActionTool].ResolveActionTimeout(TimeSpan.FromSeconds(30)));
    }

    /// <summary>
    /// The shipped file sets no timeout. Loaded through the real file repository, every tool resolves
    /// no value, the snapshot it persists is the file's own content with no key added, and the hash it
    /// reports is the hash of that content: nothing the new setting introduced reaches what is hashed.
    /// The same file with one timeout added hashes differently, so a hash only moves when an operator
    /// actually sets the key.
    /// </summary>
    [Fact]
    public async Task FileRepository_ShippedConfigurationWithoutTheKey_HashesExactlyTheFileContent()
    {
        using var fixture = ConfigDirectory.CopyShipped();
        var fileNode = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ConfigPath, TestContext.Current.CancellationToken))!;
        foreach (var (_, tool) in (JsonObject)fileNode["Tools"]!)
        {
            Assert.False(((JsonObject)tool!).ContainsKey("TimeoutSeconds"));
        }

        var snapshots = new CapturingSnapshotStore();
        var configuration = await CreateRepository(fixture.RootPath, snapshots)
            .GetCurrentAsync(TestContext.Current.CancellationToken);

        Assert.All(configuration.Tools.Values, tool => Assert.Null(tool.TimeoutSeconds));
        EnvironmentPlaceholderExpander.Expand(fileNode);
        Assert.True(JsonNode.DeepEquals(fileNode, snapshots.ConfigNode));
        Assert.Equal(
            CanonicalJsonSerializer.ComputeSha256Hex(
                CanonicalJsonSerializer.Canonicalize(fileNode),
                CanonicalJsonSerializer.Canonicalize(snapshots.InstructionsNode!)),
            configuration.ConfigHash);

        var withTimeout = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(fixture.ConfigPath, TestContext.Current.CancellationToken))!;
        ((JsonObject)withTimeout["Tools"]![ReadTool]!)["TimeoutSeconds"] = 120;
        await File.WriteAllTextAsync(fixture.ConfigPath, withTimeout.ToJsonString(), TestContext.Current.CancellationToken);
        var changed = await CreateRepository(fixture.RootPath, new CapturingSnapshotStore())
            .GetCurrentAsync(TestContext.Current.CancellationToken);

        Assert.Equal(120, changed.Tools[ReadTool].TimeoutSeconds);
        Assert.NotEqual(configuration.ConfigHash, changed.ConfigHash);
    }

    public static TheoryData<string, int?, bool> SchemaCases => new()
    {
        { ReadTool, null, true },
        { ReadTool, 1, true },
        { ReadTool, 3600, true },
        { ActionTool, 30, true },
        { ReadTool, 0, false },
        { ReadTool, 3601, false },
        { ActionTool, 0, false },
        { ActionTool, 3601, false }
    };

    [Theory]
    [MemberData(nameof(SchemaCases))]
    public void PublishedSchema_AcceptsTheSameRangeAsLoadValidation(string toolName, int? seconds, bool expectedValid)
    {
        var configuration = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(
            RepositoryRootLocator.Find(),
            "config",
            "incidentcompass.config.json")))!;
        if (seconds is not null)
        {
            ((JsonObject)configuration["Tools"]![toolName]!)["TimeoutSeconds"] = seconds;
        }

        Assert.Equal(expectedValid, TriageConfigSchemaTests.EvaluateFixture(configuration).IsValid);
    }

    private static TriageConfiguration Materialize(JsonObject node) =>
        OrchestratorAttemptDurationLoadValidationTests.CreateMaterializer().Materialize(
            "hash-1", node, OrchestratorAttemptDurationLoadValidationTests.ResolvedReferences());

    private static JsonObject ConfigNode()
    {
        var node = OrchestratorAttemptDurationLoadValidationTests.ConfigNode();
        ((JsonObject)node["Tools"]!)[ActionTool] = new JsonObject
        {
            ["Kind"] = "external_action",
            ["Category"] = "ticket_create",
            ["LogicalTargetId"] = TicketCreateTool.LogicalTargetId,
            ["Mode"] = "disabled"
        };
        return node;
    }

    private static JsonObject Tool(JsonObject node, string toolName) =>
        (JsonObject)((JsonObject)node["Tools"]!)[toolName]!;

    private static FileTriageConfigurationRepository CreateRepository(
        string contentRootPath,
        CapturingSnapshotStore snapshots)
    {
        var registry = new SignalNormalizerRegistry([
            new TesterSignalNormalizer(),
            new OtelShapedSignalNormalizer(),
            new UserReportSignalNormalizer()
        ]);
        var tools = new AgentToolRegistry([
            new AgentToolDescriptor("memory_search", AgentToolCapability.ImmediateRead),
            new AgentToolDescriptor("source_lookup", AgentToolCapability.ImmediateRead),
            new AgentToolDescriptor("ticket_search", AgentToolCapability.ImmediateRead),
            TicketCreateTool.Descriptor,
            RemediationDiffToolDescriptor.Descriptor,
            RemediationApplyToolDescriptor.Descriptor,
            BranchPushToolDescriptor.Descriptor,
            PullRequestToolDescriptor.Descriptor,
            TicketBacklinkDescriptor.Descriptor
        ]);
        return new FileTriageConfigurationRepository(
            new ContentRootEnvironment(contentRootPath),
            Options.Create(new TriageConfigSourceOptions
            {
                Kind = "File",
                Path = Path.Combine("config", "incidentcompass.config.json")
            }),
            new TriageConfigurationMaterializer(
                new TriageConfigurationLoadValidator(registry, tools, new EnvironmentModelProviderSecretReader())),
            snapshots);
    }

    private sealed class CapturingSnapshotStore : ITriageConfigurationSnapshotStore
    {
        public JsonNode? ConfigNode { get; private set; }

        public JsonObject? InstructionsNode { get; private set; }

        public Task PersistAsync(
            string configHash,
            JsonNode configNode,
            JsonObject instructionsNode,
            CancellationToken cancellationToken)
        {
            ConfigNode = configNode;
            InstructionsNode = instructionsNode;
            return Task.CompletedTask;
        }

        public Task<TriageConfigurationSnapshotDocument?> GetAsync(
            string configHash,
            CancellationToken cancellationToken) =>
            Task.FromResult<TriageConfigurationSnapshotDocument?>(null);
    }

    private sealed class ContentRootEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "IncidentCompass.UnitTests";

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class ConfigDirectory : IDisposable
    {
        private ConfigDirectory(string rootPath)
        {
            RootPath = rootPath;
            ConfigPath = Path.Combine(rootPath, "config", "incidentcompass.config.json");
        }

        public string RootPath { get; }

        public string ConfigPath { get; }

        public static ConfigDirectory CopyShipped()
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "IncidentCompass", "tool-timeout-hash", Guid.NewGuid().ToString("N"));
            var sourcePath = Path.Combine(RepositoryRootLocator.Find(), "config");
            foreach (var sourceFile in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
            {
                var destinationPath = Path.Combine(rootPath, "config", Path.GetRelativePath(sourcePath, sourceFile));
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Copy(sourceFile, destinationPath);
            }

            return new ConfigDirectory(rootPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
