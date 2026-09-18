using System.Collections;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.Resilience;
using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.Infrastructure.Relevance.LocalOnnx;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The local relevance-judge adapter end to end on the committed fixture model: pair tokenization,
/// the ONNX run, the single score it reads back, candidate order, cancellation, and every refusal
/// with its code. The scores come from the fixture generator, which ran the same graph through the
/// reference runtime.
/// </summary>
public sealed class LocalOnnxRelevanceJudgeAdapterTests : IAsyncLifetime
{
    /// <summary>
    /// Float32 arithmetic is reproducible for one graph, but the .NET and Python runtimes are two
    /// builds of it, so the recorded scores are matched to a tolerance rather than bit for bit. The
    /// scores are of order one, so this is about four decimal digits.
    /// </summary>
    private const double ScoreTolerance = 1e-4;

    private LocalOnnxRelevanceJudgeFixtureModel? fixture;
    private LocalOnnxRelevanceJudgeRuntime? runtime;

    private LocalOnnxRelevanceJudgeFixtureModel Fixture => fixture!;

    private LocalOnnxRelevanceJudgeRuntime Runtime => runtime!;

    public async ValueTask InitializeAsync()
    {
        fixture = await LocalOnnxRelevanceJudgeFixtureModel.InstallAsync(TestContext.Current.CancellationToken);
        runtime = new LocalOnnxRelevanceJudgeRuntime(Options.Create(fixture.Options));
    }

    public ValueTask DisposeAsync()
    {
        runtime?.Dispose();
        fixture?.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Score_ReturnsOneScorePerCandidateInOrderMatchingTheFixture()
    {
        var cases = LocalOnnxRelevanceJudgeFixtureModel.ReadExpectedCases()
            .Where(expected => string.Equals(expected.Query, "checkout timeout", StringComparison.Ordinal))
            .ToArray();
        Assert.True(cases.Length > 1);

        var scores = await CreateClient().ScoreAsync(
            cases[0].Query,
            cases.Select(static expected => expected.Passage).ToArray(),
            TestContext.Current.CancellationToken);

        Assert.Equal(cases.Length, scores.Count);
        for (var index = 0; index < cases.Length; index++)
        {
            Assert.Equal(cases[index].Score, scores[index], ScoreTolerance);
        }
    }

    [Fact]
    public async Task Score_ScoresEveryRecordedPairAsTheFixtureRecordedIt()
    {
        var client = CreateClient();

        foreach (var expected in LocalOnnxRelevanceJudgeFixtureModel.ReadExpectedCases())
        {
            var scores = await client.ScoreAsync(
                expected.Query,
                [expected.Passage],
                TestContext.Current.CancellationToken);

            Assert.Equal(expected.Score, Assert.Single(scores), ScoreTolerance);
        }
    }

    /// <summary>
    /// A caller whose retrieval returned nothing pays for nothing: the reader is one that would fail
    /// if it were asked, and the call still succeeds.
    /// </summary>
    [Fact]
    public async Task Score_WithNoCandidates_ReturnsAnEmptyResultWithoutLoadingAnything()
    {
        using var emptyDirectory = new LocalOnnxTestDirectory();
        var client = new LocalOnnxRelevanceJudgeClient(
            ReaderOver(emptyDirectory.FullPath),
            Runtime,
            Options.Create(Fixture.Options));

        var scores = await client.ScoreAsync("checkout timeout", [], TestContext.Current.CancellationToken);

        Assert.Empty(scores);
    }

    /// <summary>
    /// The token is honoured between pairs, not only inside one run. The candidate list cancels when
    /// its first element is read, and the call stops without ever reading the second: a long
    /// candidate set therefore stops promptly instead of finishing the set first.
    /// </summary>
    [Fact]
    public async Task Score_WhenCancelledBetweenPairs_StopsBeforeScoringThemAll()
    {
        using var cancellation = new CancellationTokenSource();
        var candidates = new CancellingCandidateList(
            cancellation,
            [.. Enumerable.Range(0, 16).Select(static index => "candidate passage number " + index)]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateClient().ScoreAsync("checkout timeout", candidates, cancellation.Token));

        Assert.Equal(1, candidates.ReadCount);
    }

    [Fact]
    public async Task Score_WithAnAlreadyCancelledToken_IsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateClient().ScoreAsync("checkout timeout", ["payment latency"], cancellation.Token));
    }

    /// <summary>
    /// A graph that declares no <c>logits</c> output is refused when it is loaded, before any pair is
    /// tokenized. The embedding fixture model is exactly such a graph: same inputs, an output named
    /// <c>last_hidden_state</c>.
    /// </summary>
    [Fact]
    public async Task Score_WithAGraphThatDeclaresNoLogitsOutput_FailsAtLoadWithTheLoadCode()
    {
        var embeddingManifest = await LocalOnnxFixtureModel.ReadFixtureManifestAsync(
            TestContext.Current.CancellationToken);
        var withoutLogits = Fixture.Installed with
        {
            ModelFilePath = Path.Combine(LocalOnnxFixtureModel.FixtureDirectory, embeddingManifest.ModelFile.Path)
        };

        var exception = await Assert.ThrowsAsync<MemoryRelevanceJudgeException>(() =>
            CreateClient(installed: withoutLogits).ScoreAsync(
                "checkout timeout",
                ["payment latency"],
                TestContext.Current.CancellationToken));

        Assert.Equal(LocalOnnxRelevanceJudgeProvider.ModelLoadFailedErrorCode, exception.ErrorCode);
        Assert.Contains(LocalOnnxLoadedRelevanceJudge.LogitsName, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A graph whose <c>logits</c> carry one score per token cannot be read as one score per pair.
    /// The shape is only known once the graph has run, so this is refused at inference, by name,
    /// rather than by taking element zero of whatever came back.
    /// </summary>
    [Fact]
    public async Task Score_WithAnOutputThatIsNotOneScorePerRow_FailsAtInferenceWithTheShapeCode()
    {
        var wrongShape = Fixture.Installed with
        {
            ModelFilePath = LocalOnnxRelevanceJudgeFixtureModel.WrongShapeModelPath
        };

        var exception = await Assert.ThrowsAsync<MemoryRelevanceJudgeException>(() =>
            CreateClient(installed: wrongShape).ScoreAsync(
                "checkout timeout",
                ["payment latency"],
                TestContext.Current.CancellationToken));

        Assert.Equal(LocalOnnxRelevanceJudgeProvider.OutputShapeInvalidErrorCode, exception.ErrorCode);
        Assert.Equal(ProviderFailureKind.InvalidResponse, exception.FailureKind);
        Assert.DoesNotContain("payment latency", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Score_WhenTheModelFileCannotBeLoaded_IsRefusedWithTheLoadCode()
    {
        var unloadable = Fixture.Installed with { ModelFilePath = Fixture.Installed.TokenizerFilePath };

        var exception = await Assert.ThrowsAsync<MemoryRelevanceJudgeException>(() =>
            CreateClient(installed: unloadable).ScoreAsync(
                "checkout timeout",
                ["payment latency"],
                TestContext.Current.CancellationToken));

        Assert.Equal(LocalOnnxRelevanceJudgeProvider.ModelLoadFailedErrorCode, exception.ErrorCode);
        Assert.Equal(ProviderFailureKind.Unavailable, exception.FailureKind);
    }

    /// <summary>
    /// A window the graph cannot serve is an inference failure, not a silently shorter sequence: the
    /// manifest claims more room than the fixture graph's position table has.
    /// </summary>
    [Fact]
    public async Task Score_WithAWindowLargerThanTheGraph_FailsWithTheInferenceCode()
    {
        var overstated = Fixture.Installed with
        {
            Manifest = Fixture.Installed.Manifest with { MaxTokens = 512 }
        };

        var exception = await Assert.ThrowsAsync<MemoryRelevanceJudgeException>(() =>
            CreateClient(installed: overstated).ScoreAsync(
                string.Concat(Enumerable.Repeat("checkout timeout ", 200)),
                [string.Concat(Enumerable.Repeat("payment latency ", 200))],
                TestContext.Current.CancellationToken));

        Assert.Equal(LocalOnnxRelevanceJudgeProvider.InferenceFailedErrorCode, exception.ErrorCode);
    }

    [Fact]
    public async Task Score_BeforeAnyInstall_IsRefusedAsUnavailable()
    {
        using var emptyDirectory = new LocalOnnxTestDirectory();
        var client = new LocalOnnxRelevanceJudgeClient(
            ReaderOver(emptyDirectory.FullPath),
            Runtime,
            Options.Create(Fixture.Options));

        var exception = await Assert.ThrowsAsync<MemoryRelevanceJudgeException>(() =>
            client.ScoreAsync("checkout timeout", ["payment latency"], TestContext.Current.CancellationToken));

        Assert.Equal(MemoryRelevanceJudgeErrorCodes.Unavailable, exception.ErrorCode);
        Assert.Equal(LocalOnnxRelevanceJudgeProvider.ModelNotInstalledErrorCode, exception.ProviderErrorCode);
        Assert.Equal(ProviderFailureKind.ConfigurationRequired, exception.FailureKind);
        Assert.False(ProviderOutageExceptionClassifier.IsProviderOutage(exception));
    }

    [Fact]
    public async Task Score_AfterAFailedInstall_IsRefusedAsUnavailableWithTheInstallCode()
    {
        var state = new LocalOnnxModelInstallState();
        state.RecordFailed(LocalOnnxModelErrorCodes.DigestMismatch, "The onnx file has the wrong digest.");

        var exception = await Assert.ThrowsAsync<MemoryRelevanceJudgeException>(() =>
            CreateClient(state).ScoreAsync(
                "checkout timeout",
                ["payment latency"],
                TestContext.Current.CancellationToken));

        Assert.Equal(MemoryRelevanceJudgeErrorCodes.Unavailable, exception.ErrorCode);
        Assert.Equal(LocalOnnxModelErrorCodes.DigestMismatch, exception.ProviderErrorCode);
        Assert.Equal(ProviderFailureKind.ConfigurationRequired, exception.FailureKind);
    }

    /// <summary>
    /// An installed judge other than the configured one is a configuration state an operator fixes,
    /// not a provider outage, and the adapter never scores against it.
    /// </summary>
    [Fact]
    public async Task Score_WhenTheInstalledJudgeIsNotTheConfiguredOne_IsRefusedAsAConfigurationState()
    {
        var options = LocalOnnxRelevanceJudgeFixtureModel.OptionsFor(
            Fixture.FixtureManifest,
            Fixture.Options.ModelDirectory!,
            modelId: "BAAI/bge-reranker-v2-gemma");
        var client = new LocalOnnxRelevanceJudgeClient(
            ReaderOver(Fixture.Options.ModelDirectory!, InstalledState()),
            Runtime,
            Options.Create(options));

        var exception = await Assert.ThrowsAsync<MemoryRelevanceJudgeException>(() =>
            client.ScoreAsync("checkout timeout", ["payment latency"], TestContext.Current.CancellationToken));

        Assert.Equal(MemoryRelevanceJudgeErrorCodes.Mismatch, exception.ErrorCode);
        Assert.Equal(LocalOnnxRelevanceJudgeProvider.ModelMismatchErrorCode, exception.ProviderErrorCode);
        Assert.Equal(ProviderFailureKind.ConfigurationRequired, exception.FailureKind);
        Assert.False(ProviderOutageExceptionClassifier.IsProviderOutage(exception));
        Assert.Contains("BAAI/bge-reranker-v2-gemma", exception.Message, StringComparison.Ordinal);
        Assert.Contains(Fixture.FixtureManifest.Id, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A model directory that holds an embedding model is the same configuration state: the manifest
    /// is readable and is simply not a judge.
    /// </summary>
    [Fact]
    public async Task Score_WhenTheInstalledModelIsNotAJudge_IsRefusedAsAConfigurationState()
    {
        var embeddingKind = Fixture.Installed with
        {
            Manifest = Fixture.Installed.Manifest with { Kind = LocalOnnxModelManifest.EmbeddingKind }
        };

        var exception = await Assert.ThrowsAsync<MemoryRelevanceJudgeException>(() =>
            CreateClient(installed: embeddingKind).ScoreAsync(
                "checkout timeout",
                ["payment latency"],
                TestContext.Current.CancellationToken));

        Assert.Equal(MemoryRelevanceJudgeErrorCodes.Mismatch, exception.ErrorCode);
        Assert.Equal(ProviderFailureKind.ConfigurationRequired, exception.FailureKind);
        Assert.Contains(LocalOnnxModelManifest.EmbeddingKind, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Score_RefusesABlankQueryAndANullCandidateList()
    {
        var client = CreateClient();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ScoreAsync("   ", ["payment latency"], TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.ScoreAsync("checkout timeout", null!, TestContext.Current.CancellationToken));
    }

    private LocalOnnxModelInstallState InstalledState(LocalOnnxInstalledModel? installed = null)
    {
        var state = new LocalOnnxModelInstallState();
        state.RecordInstalled(installed ?? Fixture.Installed);
        return state;
    }

    private static LocalOnnxInstalledRelevanceJudgeReader ReaderOver(
        string modelDirectory,
        LocalOnnxModelInstallState? state = null) =>
        new(
            Options.Create(new LocalOnnxRelevanceJudgeOptions { ModelDirectory = modelDirectory }),
            state ?? new LocalOnnxModelInstallState(),
            LocalOnnxTestArtifacts.Store(ScriptedHttpMessageHandler.Refusing()));

    private LocalOnnxRelevanceJudgeClient CreateClient(
        LocalOnnxModelInstallState? state = null,
        LocalOnnxInstalledModel? installed = null) =>
        new(
            ReaderOver(Fixture.Options.ModelDirectory!, state ?? InstalledState(installed)),
            Runtime,
            Options.Create(Fixture.Options));

    /// <summary>
    /// A candidate list that cancels the call when its first element is read, so "the token was
    /// checked before the next pair" is asserted deterministically rather than by racing a timer.
    /// </summary>
    private sealed class CancellingCandidateList(CancellationTokenSource cancellation, string[] candidates)
        : IReadOnlyList<string>
    {
        public int ReadCount { get; private set; }

        public int Count => candidates.Length;

        public string this[int index]
        {
            get
            {
                ReadCount++;
                cancellation.Cancel();
                return candidates[index];
            }
        }

        public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)candidates).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
