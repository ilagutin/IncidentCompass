using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.Infrastructure.Embeddings.LocalOnnx;
using IncidentCompass.Infrastructure.Memory;
using IncidentCompass.Infrastructure.Relevance;
using IncidentCompass.Infrastructure.Relevance.LocalOnnx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

/// <summary>
/// <c>memory model status</c> and <c>memory model install</c> against the committed fixture models on
/// disk: exit codes and what they print about the installed embedding model, the configured route,
/// the active corpus and the installed relevance judge.
/// </summary>
/// <remarks>
/// One command reports and installs both models, so every test needs a judge as well as an embedding
/// model. The committed judge fixture is installed once per test into a temporary directory of its
/// own and handed to <see cref="Services" /> by default, which keeps the judge out of the way of the
/// embedding assertions; the tests that are about the judge pass their own options instead.
/// </remarks>
public sealed class MemoryModelCommandTests : IAsyncLifetime
{
    private LocalOnnxRelevanceJudgeFixtureModel? judgeFixture;

    private LocalOnnxRelevanceJudgeFixtureModel JudgeFixture => judgeFixture!;

    public async ValueTask InitializeAsync() =>
        judgeFixture = await LocalOnnxRelevanceJudgeFixtureModel.InstallAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync()
    {
        judgeFixture?.Dispose();
        return ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData("memory", "status")]
    [InlineData("memory", "model", "frobnicate")]
    [InlineData("memory", "models", "status")]
    public async Task RunIfRequested_IgnoresEverythingElse(params string[] args)
    {
        using var directory = new LocalOnnxTestDirectory();
        await using var services = Services(
            new LocalOnnxEmbeddingOptions { ModelDirectory = directory.FullPath },
            LocalModelTestSupport.Configuration(),
            EmptyCorpus());

        Assert.Null(await MemoryModelCommand.RunIfRequestedAsync(
            args,
            services,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Status_WithNothingInstalled_ExitsOneAndSaysSo()
    {
        using var directory = new LocalOnnxTestDirectory();
        await using var services = Services(
            new LocalOnnxEmbeddingOptions { ModelDirectory = directory.FullPath },
            LocalModelTestSupport.Configuration(),
            EmptyCorpus());

        var result = await RunAsync(services, "memory", "model", "status");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Local embedding model: not installed", result.Output, StringComparison.Ordinal);
        Assert.Contains("kind=LocalOnnx", result.Output, StringComparison.Ordinal);
        Assert.Contains("memory model install", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_WhenTheInstalledModelRouteAndCorpusAgree_ExitsZeroAndPrintsIdRevisionAndLicense()
    {
        using var fixture = await LocalOnnxFixtureModel.InstallAsync(TestContext.Current.CancellationToken);
        var manifest = fixture.FixtureManifest;
        var identity = LocalOnnxModelIdentity.Describe(manifest);
        await using var services = Services(
            fixture.Options,
            LocalModelTestSupport.Configuration(manifest.Id),
            LocalModelTestSupport.Corpus(identity, Guid.NewGuid()));

        var result = await RunAsync(services, "memory", "model", "status");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Local embedding model: " + manifest.Id, result.Output, StringComparison.Ordinal);
        Assert.Contains("revision=" + manifest.Revision + " license=" + manifest.License, result.Output, StringComparison.Ordinal);
        Assert.Contains("identity=" + identity, result.Output, StringComparison.Ordinal);
        Assert.Contains("model file sha256=" + manifest.ModelFile.Sha256, result.Output, StringComparison.Ordinal);
        Assert.Contains("tokenizer file sha256=" + manifest.TokenizerFile.Sha256, result.Output, StringComparison.Ordinal);
        Assert.Contains("Active corpus model: " + identity, result.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Error);
    }

    [Fact]
    public async Task Status_WhenTheInstalledIdIsNotTheRouteModel_ExitsOne()
    {
        using var fixture = await LocalOnnxFixtureModel.InstallAsync(TestContext.Current.CancellationToken);
        await using var services = Services(
            fixture.Options,
            LocalModelTestSupport.Configuration("intfloat/multilingual-e5-base"),
            LocalModelTestSupport.Corpus(LocalOnnxModelIdentity.Describe(fixture.FixtureManifest), Guid.NewGuid()));

        var result = await RunAsync(services, "memory", "model", "status");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("is not the model route", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_WhenTheCorpusWasBuiltWithAnotherModelFile_ExitsOne()
    {
        using var fixture = await LocalOnnxFixtureModel.InstallAsync(TestContext.Current.CancellationToken);
        var otherFile = LocalModelTestSupport.Encoded(LocalModelTestSupport.SecondDigest, fixture.FixtureManifest.Id);
        await using var services = Services(
            fixture.Options,
            LocalModelTestSupport.Configuration(fixture.FixtureManifest.Id),
            LocalModelTestSupport.Corpus(otherFile, Guid.NewGuid()));

        var result = await RunAsync(services, "memory", "model", "status");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Active corpus model: " + otherFile, result.Output, StringComparison.Ordinal);
        Assert.Contains("memory rebuild", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_OnAnotherEmbeddingProvider_ExitsOne()
    {
        using var directory = new LocalOnnxTestDirectory();
        await using var services = Services(
            new LocalOnnxEmbeddingOptions { ModelDirectory = directory.FullPath },
            LocalModelTestSupport.Configuration(),
            EmptyCorpus(),
            provider: "OpenAiCompatible");

        var result = await RunAsync(services, "memory", "model", "status");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("LocalOnnx", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_OnPreplacedFiles_InstallsAndSaysThereIsNoPreviousManifest()
    {
        using var directory = new LocalOnnxTestDirectory();
        var manifest = await LocalOnnxFixtureModel.ReadFixtureManifestAsync(TestContext.Current.CancellationToken);
        LocalOnnxFixtureModel.PlaceFixtureFiles(manifest, directory.FullPath);
        await using var services = Services(
            LocalOnnxFixtureModel.OptionsFor(manifest, directory.FullPath),
            LocalModelTestSupport.Configuration(manifest.Id),
            EmptyCorpus());

        var result = await RunAsync(services, "memory", "model", "install");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Installed local embedding model: " + manifest.Id, result.Output, StringComparison.Ordinal);
        Assert.Contains("revision=" + manifest.Revision + " license=" + manifest.License, result.Output, StringComparison.Ordinal);
        Assert.Contains("no previous manifest", result.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(LocalOnnxModelLayout.GetManifestPath(directory.FullPath)));
    }

    [Fact]
    public async Task Install_BesideAnInstalledModel_SaysWhereThePreviousManifestIsAndHowToRollBack()
    {
        using var fixture = await LocalOnnxFixtureModel.InstallAsync(TestContext.Current.CancellationToken);
        var renamed = fixture.FixtureManifest with { Id = "incidentcompass/fixture-embedding-model-renamed" };
        await using var services = Services(
            LocalOnnxFixtureModel.OptionsFor(renamed, fixture.Options.ModelDirectory!),
            LocalModelTestSupport.Configuration(renamed.Id),
            EmptyCorpus());

        var result = await RunAsync(services, "memory", "model", "install");

        var previousManifestPath = LocalOnnxModelLayout.GetPreviousManifestPath(Path.GetFullPath(fixture.Options.ModelDirectory!));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Installed local embedding model: " + renamed.Id, result.Output, StringComparison.Ordinal);
        Assert.Contains("kept at " + previousManifestPath, result.Output, StringComparison.Ordinal);
        Assert.Contains("Roll back: copy " + previousManifestPath, result.Output, StringComparison.Ordinal);
        Assert.Contains("and restart the Worker, which keeps the model it verified at start", result.Output, StringComparison.Ordinal);
        Assert.Contains("run 'memory rebuild' to re-embed the corpus with it, and restart the Worker", result.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(previousManifestPath));
    }

    [Fact]
    public async Task Install_WhoseDownloadNeverCompletes_EndsWithTheTimeoutCodeWithinItsBound()
    {
        using var directory = new LocalOnnxTestDirectory();
        var manifest = await LocalOnnxFixtureModel.ReadFixtureManifestAsync(TestContext.Current.CancellationToken);
        using var stalling = new ScriptedHttpMessageHandler(static async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        });
        await using var services = Services(
            LocalOnnxFixtureModel.OptionsFor(manifest, directory.FullPath, installTimeoutSeconds: 1),
            LocalModelTestSupport.Configuration(manifest.Id),
            EmptyCorpus(),
            handler: stalling);
        var started = System.Diagnostics.Stopwatch.StartNew();

        var result = await RunAsync(services, "memory", "model", "install");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(LocalOnnxModelErrorCodes.InstallTimedOut, result.Error, StringComparison.Ordinal);
        Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(15));
        Assert.False(File.Exists(LocalOnnxModelLayout.GetManifestPath(directory.FullPath)));
        Assert.Empty(Directory.EnumerateFiles(directory.FullPath, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Install_WhenTheConfiguredModelIsActive_SaysNothingChanged()
    {
        using var fixture = await LocalOnnxFixtureModel.InstallAsync(TestContext.Current.CancellationToken);
        await using var services = Services(
            fixture.Options,
            LocalModelTestSupport.Configuration(fixture.FixtureManifest.Id),
            EmptyCorpus());

        var result = await RunAsync(services, "memory", "model", "install");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("already active", result.Output, StringComparison.Ordinal);
        Assert.Contains("nothing was changed", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_WithAFileThatFailsVerification_ExitsOneWithTheCode()
    {
        using var directory = new LocalOnnxTestDirectory();
        var manifest = await LocalOnnxFixtureModel.ReadFixtureManifestAsync(TestContext.Current.CancellationToken);
        var modelPath = Path.Combine(
            directory.FullPath,
            LocalOnnxModelLayout.GetArtifactRelativePath(manifest.ModelFile.Sha256, manifest.ModelFile.Url));
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        await File.WriteAllBytesAsync(modelPath, [1, 2, 3], TestContext.Current.CancellationToken);
        await using var services = Services(
            LocalOnnxFixtureModel.OptionsFor(manifest, directory.FullPath),
            LocalModelTestSupport.Configuration(manifest.Id),
            EmptyCorpus());

        var result = await RunAsync(services, "memory", "model", "install");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(LocalOnnxModelErrorCodes.DigestMismatch, result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(LocalOnnxModelLayout.GetManifestPath(directory.FullPath)));
    }

    [Fact]
    public async Task Status_ReportsTheEmbeddingModelAndTheRelevanceJudge()
    {
        using var fixture = await LocalOnnxFixtureModel.InstallAsync(TestContext.Current.CancellationToken);
        var manifest = fixture.FixtureManifest;
        await using var services = Services(
            fixture.Options,
            LocalModelTestSupport.Configuration(manifest.Id),
            LocalModelTestSupport.Corpus(LocalOnnxModelIdentity.Describe(manifest), Guid.NewGuid()));

        var result = await RunAsync(services, "memory", "model", "status");

        var judge = JudgeFixture.FixtureManifest;
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Local embedding model: " + manifest.Id, result.Output, StringComparison.Ordinal);
        Assert.Contains("Local relevance judge: " + judge.Id, result.Output, StringComparison.Ordinal);
        Assert.Contains(
            "judge revision=" + judge.Revision + " license=" + judge.License,
            result.Output,
            StringComparison.Ordinal);
        Assert.Contains("judge model file sha256=" + judge.ModelFile.Sha256, result.Output, StringComparison.Ordinal);
        Assert.Contains(
            "judge tokenizer file sha256=" + judge.TokenizerFile.Sha256,
            result.Output,
            StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Error);
    }

    [Fact]
    public async Task Status_WhenOnlyTheJudgeIsMissing_ExitsOneAndStillReportsTheEmbeddingModel()
    {
        using var fixture = await LocalOnnxFixtureModel.InstallAsync(TestContext.Current.CancellationToken);
        using var emptyJudgeDirectory = new LocalOnnxTestDirectory();
        var manifest = fixture.FixtureManifest;
        await using var services = Services(
            fixture.Options,
            LocalModelTestSupport.Configuration(manifest.Id),
            LocalModelTestSupport.Corpus(LocalOnnxModelIdentity.Describe(manifest), Guid.NewGuid()),
            judgeOptions: LocalOnnxRelevanceJudgeFixtureModel.OptionsFor(
                JudgeFixture.FixtureManifest,
                emptyJudgeDirectory.FullPath));

        var result = await RunAsync(services, "memory", "model", "status");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Local embedding model: " + manifest.Id, result.Output, StringComparison.Ordinal);
        Assert.Contains("Local relevance judge: not installed", result.Output, StringComparison.Ordinal);
        Assert.Contains("No local relevance judge is installed.", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_WhenTheJudgeHasNoModelDirectory_SaysItIsNotConfigured()
    {
        using var fixture = await LocalOnnxFixtureModel.InstallAsync(TestContext.Current.CancellationToken);
        var manifest = fixture.FixtureManifest;
        await using var services = Services(
            fixture.Options,
            LocalModelTestSupport.Configuration(manifest.Id),
            LocalModelTestSupport.Corpus(LocalOnnxModelIdentity.Describe(manifest), Guid.NewGuid()),
            judgeOptions: new LocalOnnxRelevanceJudgeOptions());

        var result = await RunAsync(services, "memory", "model", "status");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Local relevance judge: not configured", result.Output, StringComparison.Ordinal);
        Assert.Contains("RelevanceJudge:LocalOnnx:ModelDirectory", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A Worker that runs the mock judge is reported as running the mock, not as a local judge that is
    /// not configured, which would be false while a judge is confirming matches. The local judge's
    /// directory is deliberately blank here, as it is on the mock stack, to show the mock is recognized
    /// before the local settings are read. It exits 1 with an otherwise healthy embedding model: the
    /// mock is not the shipped judge, and a script waiting for a ready host must not accept it.
    /// </summary>
    [Fact]
    public async Task Status_OnAMockJudgeHost_ReportsTheMockAndExitsOne()
    {
        using var fixture = await LocalOnnxFixtureModel.InstallAsync(TestContext.Current.CancellationToken);
        var manifest = fixture.FixtureManifest;
        await using var services = Services(
            fixture.Options,
            LocalModelTestSupport.Configuration(manifest.Id),
            LocalModelTestSupport.Corpus(LocalOnnxModelIdentity.Describe(manifest), Guid.NewGuid()),
            judgeOptions: new LocalOnnxRelevanceJudgeOptions(),
            judgeProvider: RelevanceJudgeOptions.MockProvider);

        var result = await RunAsync(services, "memory", "model", "status");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Local embedding model: " + manifest.Id, result.Output, StringComparison.Ordinal);
        Assert.Contains(RelevanceJudgeModelSection.MockJudgeStatusLine, result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("not configured", result.Output, StringComparison.Ordinal);
        Assert.Contains("must never run on a production host", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_OnAMockJudgeHost_InstallsNoJudgeReportsTheMockAndExitsOne()
    {
        using var fixture = await LocalOnnxFixtureModel.InstallAsync(TestContext.Current.CancellationToken);
        await using var services = Services(
            fixture.Options,
            LocalModelTestSupport.Configuration(fixture.FixtureManifest.Id),
            EmptyCorpus(),
            judgeOptions: new LocalOnnxRelevanceJudgeOptions(),
            judgeProvider: RelevanceJudgeOptions.MockProvider);

        var result = await RunAsync(services, "memory", "model", "install");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("already active", result.Output, StringComparison.Ordinal);
        Assert.Contains(RelevanceJudgeModelSection.MockJudgeStatusLine, result.Output, StringComparison.Ordinal);
        Assert.Contains("no relevance judge model to install", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_InstallsBothTheEmbeddingModelAndTheRelevanceJudge()
    {
        using var modelDirectory = new LocalOnnxTestDirectory();
        using var judgeDirectory = new LocalOnnxTestDirectory();
        var manifest = await LocalOnnxFixtureModel.ReadFixtureManifestAsync(TestContext.Current.CancellationToken);
        var judge = JudgeFixture.FixtureManifest;
        LocalOnnxFixtureModel.PlaceFixtureFiles(manifest, modelDirectory.FullPath);
        LocalOnnxRelevanceJudgeFixtureModel.PlaceFixtureFiles(judge, judgeDirectory.FullPath);
        await using var services = Services(
            LocalOnnxFixtureModel.OptionsFor(manifest, modelDirectory.FullPath),
            LocalModelTestSupport.Configuration(manifest.Id),
            EmptyCorpus(),
            judgeOptions: LocalOnnxRelevanceJudgeFixtureModel.OptionsFor(judge, judgeDirectory.FullPath));

        var result = await RunAsync(services, "memory", "model", "install");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Installed local embedding model: " + manifest.Id, result.Output, StringComparison.Ordinal);
        Assert.Contains("Installed local relevance judge: " + judge.Id, result.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(LocalOnnxModelLayout.GetManifestPath(modelDirectory.FullPath)));
        Assert.True(File.Exists(LocalOnnxModelLayout.GetManifestPath(judgeDirectory.FullPath)));
        Assert.Equal(string.Empty, result.Error);
    }

    /// <summary>
    /// On a real host the options accessor is where validation runs, and it throws
    /// <see cref="OptionsValidationException" />, which is not an
    /// <see cref="InvalidOperationException" />. This command runs before the host starts, so nothing
    /// has reported those failures yet; if it does not catch them itself they escape it entirely.
    /// Both halves have that shape, so both are covered.
    /// </summary>
    [Fact]
    public async Task Status_WhenTheJudgeSectionFailsValidation_SaysItIsNotConfiguredInsteadOfThrowing()
    {
        using var fixture = await LocalOnnxFixtureModel.InstallAsync(TestContext.Current.CancellationToken);
        var manifest = fixture.FixtureManifest;
        await using var services = Services(
            fixture.Options,
            LocalModelTestSupport.Configuration(manifest.Id),
            LocalModelTestSupport.Corpus(LocalOnnxModelIdentity.Describe(manifest), Guid.NewGuid()),
            judgeAccessor: new ValidationFailingOptions<LocalOnnxRelevanceJudgeOptions>(
                "IncidentCompass:RelevanceJudge:LocalOnnx:MaxTokens must be between 5 and 8192."));

        var result = await RunAsync(services, "memory", "model", "status");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Local embedding model: " + manifest.Id, result.Output, StringComparison.Ordinal);
        Assert.Contains("Local relevance judge: not configured", result.Output, StringComparison.Ordinal);
        Assert.Contains("MaxTokens must be between 5 and 8192.", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_WhenTheEmbeddingSectionFailsValidation_ReportsTheFailuresInsteadOfThrowing()
    {
        await using var services = Services(
            new LocalOnnxEmbeddingOptions(),
            LocalModelTestSupport.Configuration(),
            EmptyCorpus(),
            embeddingAccessor: new ValidationFailingOptions<LocalOnnxEmbeddingOptions>(
                "IncidentCompass:Embeddings:LocalOnnx:ModelDirectory must be an absolute directory path."));

        var result = await RunAsync(services, "memory", "model", "status");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            "Local embedding model command failed: IncidentCompass:Embeddings:LocalOnnx:ModelDirectory",
            result.Error,
            StringComparison.Ordinal);
    }

    private static async Task<CommandResult> RunAsync(IServiceProvider services, params string[] args)
    {
        await using var output = new StringWriter();
        await using var error = new StringWriter();
        var exitCode = await MemoryModelCommand.RunIfRequestedAsync(
            args,
            services,
            output,
            error,
            TestContext.Current.CancellationToken);
        return new CommandResult(exitCode, output.ToString(), error.ToString());
    }

    private ServiceProvider Services(
        LocalOnnxEmbeddingOptions options,
        TriageConfiguration configuration,
        MemoryCorpusInventory inventory,
        string provider = "LocalOnnx",
        HttpMessageHandler? handler = null,
        LocalOnnxRelevanceJudgeOptions? judgeOptions = null,
        IOptions<LocalOnnxEmbeddingOptions>? embeddingAccessor = null,
        IOptions<LocalOnnxRelevanceJudgeOptions>? judgeAccessor = null,
        string? judgeProvider = null)
    {
        var services = new ServiceCollection();
        if (judgeProvider is not null)
        {
            services.AddSingleton(Options.Create(new RelevanceJudgeOptions { Provider = judgeProvider }));
        }

        services.AddSingleton(Options.Create(new EmbeddingOptions { Provider = provider }));
        services.AddSingleton(embeddingAccessor ?? Options.Create(options));
        services.AddSingleton(judgeAccessor ?? Options.Create(judgeOptions ?? JudgeFixture.Options));
        services.AddSingleton(Options.Create(new MemorySeedOptions { TenantId = "local", Owner = "owner" }));
        services.AddSingleton<ITriageConfigurationRepository>(new StaticConfigurationRepository(configuration));
        services.AddSingleton<IMemoryRepository>(new InventoryMemoryRepository(inventory));
        services.AddSingleton(LocalOnnxTestArtifacts.Store(handler ?? ScriptedHttpMessageHandler.Refusing()));
        return services.BuildServiceProvider();
    }

    private static MemoryCorpusInventory EmptyCorpus() => new(null, [], 0, 0);

    private sealed record CommandResult(int? ExitCode, string Output, string Error);

    /// <summary>
    /// An options accessor that behaves as a validating one does on a real host: the failures surface
    /// when <c>Value</c> is read, not when the accessor is built.
    /// </summary>
    private sealed class ValidationFailingOptions<TOptions>(params string[] failures) : IOptions<TOptions>
        where TOptions : class
    {
        public TOptions Value =>
            throw new OptionsValidationException(Options.DefaultName, typeof(TOptions), failures);
    }

    private sealed class StaticConfigurationRepository(TriageConfiguration configuration) : ITriageConfigurationRepository
    {
        public Task<TriageConfiguration> GetCurrentAsync(CancellationToken cancellationToken) => Task.FromResult(configuration);

        public Task<TriageConfiguration> GetByHashAsync(string configHash, CancellationToken cancellationToken) =>
            Task.FromResult(configuration);
    }

    private sealed class InventoryMemoryRepository(MemoryCorpusInventory inventory) : IMemoryRepository
    {
        public Task<IReadOnlyList<MemorySearchMatch>> SearchAsync(MemorySearchRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> SeedItemExistsAsync(string owner, MemorySeedItem item, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReconcileSeedCorpusAsync(MemorySeedCorpus corpus, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MemoryCorpusInventory> GetCorpusInventoryAsync(string tenantId, string owner, CancellationToken cancellationToken) =>
            Task.FromResult(inventory);
    }
}
