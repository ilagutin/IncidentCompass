using System.Text.Json.Nodes;
using IncidentCompass.Application.Governance.Tools;
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
/// Shared scaffolding for the tests that assert an optional <c>Tools</c> key changes no shipped
/// configuration hash: a private copy of <c>config/</c>, the real file repository over it, a snapshot
/// store that records what was persisted, and the materializer those tests validate through.
/// </summary>
internal static class ShippedConfigTestSupport
{
    public static TriageConfigurationMaterializer CreateMaterializer() =>
        new(new TriageConfigurationLoadValidator(
            new SignalNormalizerRegistry([
                new TesterSignalNormalizer(),
                new OtelShapedSignalNormalizer(),
                new UserReportSignalNormalizer()
            ]),
            ToolRegistry(),
            new EnvironmentModelProviderSecretReader()));

    public static AgentToolRegistry ToolRegistry() => new([
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

    public static FileTriageConfigurationRepository CreateRepository(
        string contentRootPath,
        RecordingSnapshotStore snapshots) =>
        new(
            new ContentRootTestEnvironment(contentRootPath),
            Options.Create(new TriageConfigSourceOptions
            {
                Kind = "File",
                Path = Path.Combine("config", "incidentcompass.config.json")
            }),
            CreateMaterializer(),
            snapshots);
}

/// <summary>A throwaway copy of the shipped <c>config/</c> directory a test may edit in place.</summary>
internal sealed class ShippedConfigDirectory : IDisposable
{
    private ShippedConfigDirectory(string rootPath)
    {
        RootPath = rootPath;
        ConfigPath = Path.Combine(rootPath, "config", "incidentcompass.config.json");
    }

    public string RootPath { get; }

    public string ConfigPath { get; }

    public static ShippedConfigDirectory Copy(string fixtureName)
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "IncidentCompass",
            fixtureName,
            Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(RepositoryRootLocator.Find(), "config");
        foreach (var sourceFile in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var destinationPath = Path.Combine(rootPath, "config", Path.GetRelativePath(sourcePath, sourceFile));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourceFile, destinationPath);
        }

        return new ShippedConfigDirectory(rootPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}

internal sealed class RecordingSnapshotStore : ITriageConfigurationSnapshotStore
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

internal sealed class ContentRootTestEnvironment(string contentRootPath) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;

    public string ApplicationName { get; set; } = "IncidentCompass.UnitTests";

    public string ContentRootPath { get; set; } = contentRootPath;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
