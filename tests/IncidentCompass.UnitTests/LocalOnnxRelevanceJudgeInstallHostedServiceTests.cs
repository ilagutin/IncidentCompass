using System.Net;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.Infrastructure.Relevance.LocalOnnx;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The judge's install pass at Worker start: what it says when this host runs no judge, and what it
/// records when an install fails.
/// </summary>
public sealed class LocalOnnxRelevanceJudgeInstallHostedServiceTests : IDisposable
{
    private readonly LocalOnnxTestDirectory directory = new();
    private readonly LocalOnnxRelevanceJudgeInstallState state = new();
    private readonly List<LogEntry> logEntries = [];

    public void Dispose() => directory.Dispose();

    /// <summary>
    /// A host with no judge directory installs nothing, which is correct, and used to say nothing at
    /// all, which was not: the only way to learn that a Worker runs without a judge was to run
    /// <c>memory model status</c> by hand. It has to be diagnosable from the logs alone, so the line
    /// is at Information and names the setting that would turn the judge on.
    /// </summary>
    [Fact]
    public async Task StartAsync_WithNoJudgeDirectory_SaysSoAtInformationAndFetchesNothing()
    {
        using var handler = ScriptedHttpMessageHandler.Refusing();

        await CreateService(handler, modelDirectory: null).StartAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(logEntries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(
            LocalOnnxRelevanceJudgeOptions.SectionName + ":ModelDirectory",
            entry.Message,
            StringComparison.Ordinal);
        Assert.Equal(LocalOnnxModelInstallStatus.NotStarted, state.Snapshot.Status);
        Assert.Empty(handler.RequestedUris);
    }

    /// <summary>
    /// The recorded code is the judge's own. The store answers with its own vocabulary, every constant
    /// of which is spelled <c>embedding_model_...</c>, and a judge that recorded one would name the
    /// wrong model in its state, its log line and every refusal read from it.
    /// </summary>
    [Fact]
    public async Task StartAsync_WhenTheFetchFails_RecordsAJudgeCodeRatherThanTheStoreCode()
    {
        using var handler = new ScriptedHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await CreateService(handler).StartAsync(TestContext.Current.CancellationToken);

        var snapshot = state.Snapshot;
        Assert.Equal(LocalOnnxModelInstallStatus.Failed, snapshot.Status);
        Assert.Equal(LocalOnnxRelevanceJudgeProvider.ModelFetchFailedErrorCode, snapshot.ErrorCode);
        Assert.DoesNotContain(logEntries, entry => entry.Message.Contains("embedding_model", StringComparison.Ordinal));
    }

    private LocalOnnxRelevanceJudgeInstallHostedService CreateService(
        HttpMessageHandler handler,
        string? modelDirectory = "")
    {
        var options = new LocalOnnxRelevanceJudgeOptions
        {
            ModelDirectory = modelDirectory is "" ? directory.FullPath : modelDirectory,
            InstallTimeoutSeconds = 60
        };

        return new LocalOnnxRelevanceJudgeInstallHostedService(
            Options.Create(options),
            LocalOnnxTestArtifacts.Store(handler),
            state,
            new CapturingLogger(logEntries));
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger(List<LogEntry> entries)
        : ILogger<LocalOnnxRelevanceJudgeInstallHostedService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}
