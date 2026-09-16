using System.Text.Json.Nodes;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.Intake;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The migration of the attempt ceiling key as the repository sees it: an operator file on the old
/// key loads with a warning naming the new key, a file on the new key loads silently, and a stored
/// snapshot written before the rename rehydrates for a queued job with its original ceiling and
/// without repeating the warning per job.
/// </summary>
public sealed class TriageConfigurationAttemptDurationMigrationTests : IDisposable
{
    private const int DeprecatedKeyWarningEventId = 2701;

    private readonly string directory = Path.Combine(Path.GetTempPath(), "ic-attempt-duration-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetCurrentAsync_OldKey_LoadsItsValueAndWarnsNamingTheNewKey()
    {
        var logger = new RecordingLogger<FileTriageConfigurationRepository>();
        var repository = CreateRepository(OrchestratorAttemptDurationLoadValidationTests.ConfigNode(("MaxWallClockSeconds", 600)), logger, new RecordingSnapshotStore());

        var configuration = await repository.GetCurrentAsync(TestContext.Current.CancellationToken);

        Assert.Equal(600, configuration.Orchestrator.Budget.ResolveAttemptDurationSeconds());
        var warning = logger.Single(DeprecatedKeyWarningEventId);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(OrchestratorBudgetSettings.MaxWallClockSecondsSettingName, warning.Message, StringComparison.Ordinal);
        Assert.Contains(OrchestratorBudgetSettings.MaxAttemptDurationSecondsSettingName, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetCurrentAsync_NewKey_LoadsWithoutTheDeprecationWarning()
    {
        var logger = new RecordingLogger<FileTriageConfigurationRepository>();
        var repository = CreateRepository(OrchestratorAttemptDurationLoadValidationTests.ConfigNode(("MaxAttemptDurationSeconds", 14_400)), logger, new RecordingSnapshotStore());

        var configuration = await repository.GetCurrentAsync(TestContext.Current.CancellationToken);

        Assert.Equal(14_400, configuration.Orchestrator.Budget.ResolveAttemptDurationSeconds());
        Assert.Equal(-1, logger.IndexOf(DeprecatedKeyWarningEventId));
    }

    [Fact]
    public async Task GetByHashAsync_StoredSnapshotWithTheOldKey_RehydratesWithItsCeilingAndNoWarning()
    {
        var logger = new RecordingLogger<FileTriageConfigurationRepository>();
        var store = new RecordingSnapshotStore();
        store.Documents["old-snapshot"] = new TriageConfigurationSnapshotDocument(
            "old-snapshot",
            OrchestratorAttemptDurationLoadValidationTests.ConfigNode(("MaxWallClockSeconds", 120)),
            OrchestratorAttemptDurationLoadValidationTests.ResolvedReferences());
        var repository = CreateRepository(OrchestratorAttemptDurationLoadValidationTests.ConfigNode(), logger, store);

        var configuration = await repository.GetByHashAsync("old-snapshot", TestContext.Current.CancellationToken);

        Assert.Equal(120, configuration.Orchestrator.Budget.MaxWallClockSeconds);
        Assert.Equal(TimeSpan.FromSeconds(120), configuration.Orchestrator.Budget.ResolveAttemptDurationLimit());
        Assert.Equal(-1, logger.IndexOf(DeprecatedKeyWarningEventId));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private FileTriageConfigurationRepository CreateRepository(
        JsonObject configNode,
        ILogger<FileTriageConfigurationRepository> logger,
        RecordingSnapshotStore store)
    {
        Directory.CreateDirectory(Path.Combine(directory, "instructions"));
        Directory.CreateDirectory(Path.Combine(directory, "schemas"));
        File.WriteAllText(Path.Combine(directory, "instructions", "orchestrator.md"), "orchestrator body");
        File.WriteAllText(Path.Combine(directory, "instructions", "analysis.md"), "analysis body");
        File.WriteAllText(Path.Combine(directory, "schemas", "analysis.json"), "{ \"type\": \"object\" }");
        File.WriteAllText(Path.Combine(directory, "incidentcompass.config.json"), configNode.ToJsonString());

        return new FileTriageConfigurationRepository(
            new DirectoryHostEnvironment(directory),
            Options.Create(new TriageConfigSourceOptions { Path = "incidentcompass.config.json" }),
            OrchestratorAttemptDurationLoadValidationTests.CreateMaterializer(),
            store,
            logger);
    }

    private sealed class RecordingSnapshotStore : ITriageConfigurationSnapshotStore
    {
        public Dictionary<string, TriageConfigurationSnapshotDocument> Documents { get; } = new(StringComparer.Ordinal);

        public Task PersistAsync(
            string configHash,
            JsonNode configNode,
            JsonObject instructionsNode,
            CancellationToken cancellationToken)
        {
            Documents[configHash] = new TriageConfigurationSnapshotDocument(configHash, configNode, instructionsNode);
            return Task.CompletedTask;
        }

        public Task<TriageConfigurationSnapshotDocument?> GetAsync(string configHash, CancellationToken cancellationToken) =>
            Task.FromResult(Documents.GetValueOrDefault(configHash));
    }

    private sealed class DirectoryHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "IncidentCompass.UnitTests";

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
