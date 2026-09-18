using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Intake.Normalization;
using IncidentCompass.Application.Memory;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Infrastructure.Memory;
using IncidentCompass.Infrastructure.Relevance;
using IncidentCompass.Infrastructure.Relevance.Mock;
using IncidentCompass.TestSupport;
using Microsoft.Extensions.Logging;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The mock stack's relevance judge. It is not a governance boundary; what is pinned here is that its
/// rule is the one its comment states, and that under it the shipped mock demo reaches the judged path
/// the product takes: the shipped demo timeout signal is confirmed against the shipped seed corpus, and
/// the shipped demo null-reference signal is not.
/// </summary>
public sealed class MockMemoryRelevanceJudgeTests
{
    [Theory]
    [InlineData("payments-api TimeoutException timed out", "Fails with `TimeoutException` under load", MockMemoryRelevanceJudge.Confirmed)]
    [InlineData("payments-api NullReferenceException crashed", "service name: payments-api or checkout-api", MockMemoryRelevanceJudge.Related)]
    [InlineData("payments-api NullReferenceException crashed", "inventory latency runbook", MockMemoryRelevanceJudge.Unrelated)]
    [InlineData("checkout is slow", "checkout timeout runbook", MockMemoryRelevanceJudge.Unrelated)]
    public async Task ScoreAsync_ScoresByTheErrorTypeFirstAndTheServiceNameSecond(
        string query,
        string candidate,
        float expected)
    {
        var scores = await new MockMemoryRelevanceJudge().ScoreAsync(
            query,
            [candidate],
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, Assert.Single(scores));
    }

    /// <summary>
    /// The three scores sit on the right sides of the shipped thresholds, so the rule means what its
    /// comment says under the configuration the mock stack ships with.
    /// </summary>
    [Fact]
    public void Scores_SitOnTheStatedSidesOfTheShippedThresholds()
    {
        Assert.True(MockMemoryRelevanceJudge.Confirmed >= MemoryRelevanceJudgeSetting.DefaultConfirmScore);
        Assert.True(MockMemoryRelevanceJudge.Related >= MemoryRelevanceJudgeSetting.DefaultFloorScore);
        Assert.True(MockMemoryRelevanceJudge.Related < MemoryRelevanceJudgeSetting.DefaultConfirmScore);
        Assert.True(MockMemoryRelevanceJudge.Unrelated < MemoryRelevanceJudgeSetting.DefaultFloorScore);
    }

    /// <summary>
    /// The shipped demo timeout signal, sent by the mock memory role as its query, against every chunk of
    /// the shipped seed corpus, through the real <c>memory_search</c>: a known-incident document comes
    /// back confirmed, so a memory-based known-incident report is reachable on the mock stack.
    /// </summary>
    [Fact]
    public async Task ShippedDemoTimeoutSignal_IsConfirmedAgainstTheShippedKnownIncident()
    {
        var output = await SearchSeedCorpusAsync(DemoSignal("TimeoutException"));

        Assert.Equal(MemorySearchMessage.MatchesFound, output.GetProperty("message").GetString());
        Assert.Contains(
            output.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("kind").GetString() == "known_incident" &&
                MemoryRetrievalConfidence.ConfirmsMatch(item.GetProperty("retrievalConfidence").GetString()));
    }

    /// <summary>
    /// The shipped demo null-reference signal names an error type no shipped document names, so nothing
    /// is confirmed for it: whatever comes back is related context only.
    /// </summary>
    [Fact]
    public async Task ShippedDemoNullReferenceSignal_IsNotConfirmedAgainstAnything()
    {
        var output = await SearchSeedCorpusAsync(DemoSignal("NullReferenceException"));

        Assert.NotEqual(MemorySearchMessage.MatchesFound, output.GetProperty("message").GetString());
        Assert.All(
            output.GetProperty("items").EnumerateArray(),
            item => Assert.Equal(MemoryRetrievalConfidence.Low, item.GetProperty("retrievalConfidence").GetString()));
    }

    [Theory]
    [InlineData("LocalOnnx", true)]
    [InlineData("Mock", true)]
    [InlineData("mock", true)]
    [InlineData("OpenAICompatible", false)]
    [InlineData("Onnx", false)]
    [InlineData("", false)]
    public void ProviderValidator_AcceptsOnlyTheTwoJudges(string provider, bool valid)
    {
        var result = new RelevanceJudgeOptionsValidator().Validate(
            null,
            new RelevanceJudgeOptions { Provider = provider });

        Assert.Equal(valid, result.Succeeded);
    }

    /// <summary>
    /// A mock confirmation looks like a real one in the tool output and the artifact, so a mock host
    /// says so in the log at start, at Warning, under its own event id, and names no query or score.
    /// </summary>
    [Fact]
    public async Task StartupWarning_LogsOnceAtWarningThatTheMockIsNotAGovernanceBoundary()
    {
        var logger = new CapturingLogger();

        await new MockRelevanceJudgeStartupWarning(logger).StartAsync(TestContext.Current.CancellationToken);

        var (level, eventId, message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Equal(2323, eventId);
        Assert.Contains("not a governance boundary", message, StringComparison.Ordinal);
        Assert.Contains("must never run on a production host", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDefault_IsTheLocalJudge()
    {
        Assert.Equal(RelevanceJudgeOptions.LocalOnnxProvider, new RelevanceJudgeOptions().Provider);
    }

    private static async Task<JsonElement> SearchSeedCorpusAsync(Signal signal)
    {
        var chunker = new MemoryDocumentChunker(new CharacterEstimateChunkTokenCounter(), new MemoryChunkingOptions());
        var matches = new List<MemorySearchMatch>();
        foreach (var file in MemorySeedFileLoader.Load(Path.Combine(RepositoryRootLocator.Find(), "samples")))
        {
            foreach (var chunk in chunker.Chunk(file.Title, file.Content))
            {
                var id = matches.Count + 1;
                matches.Add(new MemorySearchMatch(
                    Guid.Parse($"10000000-0000-0000-0000-{id:D12}"),
                    Guid.Parse($"20000000-0000-0000-0000-{id:D12}"),
                    file.Kind,
                    file.Source,
                    file.Title,
                    chunk.Position,
                    chunk.Text,
                    0.9,
                    file.ServiceName,
                    file.Component,
                    file.ReleaseName,
                    chunk.HeadingPath));
            }
        }

        var tool = new MemorySearchTool(
            new StubEmbeddingClient(),
            new StubMemoryRepository(matches),
            new MockMemoryRelevanceJudge());
        var validation = tool.Validate(JsonSerializer.SerializeToElement(new { query = MemoryFaultQuery.For(signal) }));
        var result = await tool.ExecuteAsync(
            MemorySearchToolTestSupport.Context(MemorySearchToolTestSupport.Configuration(), signal),
            validation.SanitizedArguments,
            TestContext.Current.CancellationToken);
        return result.Output;
    }

    /// <summary>
    /// The demo request in <c>samples/http/local-demo.http</c> whose error type is
    /// <paramref name="errorType" />, as a trigger signal with the summary intake would synthesize.
    /// </summary>
    private static Signal DemoSignal(string errorType)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRootLocator.Find(), "samples", "http", "local-demo.http"));
        var body = text.Split("###")
            .Where(block => block.Contains("\"errorType\": \"" + errorType + "\"", StringComparison.Ordinal))
            .Select(static block => JsonNode.Parse(block[block.IndexOf("\n{", StringComparison.Ordinal)..(block.LastIndexOf('}') + 1)])!)
            .Single();
        var serviceName = body["serviceName"]!.GetValue<string>();
        var attributes = body["attributes"]!;
        var errorMessage = attributes["errorMessage"]!.GetValue<string>();
        var summary = SummarySynthesizer.ForStructuredSignal(
            serviceName,
            attributes["operationName"]?.GetValue<string>(),
            attributes["httpRoute"]?.GetValue<string>(),
            errorType,
            errorMessage);
        return MemorySearchToolTestSupport.TriggerSignal(summary, errorType, errorMessage, serviceName);
    }

    private sealed class CapturingLogger : ILogger<MockRelevanceJudgeStartupWarning>
    {
        public List<(LogLevel Level, int EventId, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, eventId.Id, formatter(state, exception)));
    }
}
