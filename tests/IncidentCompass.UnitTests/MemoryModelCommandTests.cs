using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.Infrastructure.Embeddings.LocalOnnx;
using IncidentCompass.Infrastructure.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

/// <summary>
/// <c>memory model status</c> and <c>memory model install</c> against the committed fixture model on
/// disk: exit codes and what they print about the installed model, the configured route and the
/// active corpus.
/// </summary>
public sealed class MemoryModelCommandTests
{
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

    private static ServiceProvider Services(
        LocalOnnxEmbeddingOptions options,
        TriageConfiguration configuration,
        MemoryCorpusInventory inventory,
        string provider = "LocalOnnx",
        HttpMessageHandler? handler = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new EmbeddingOptions { Provider = provider }));
        services.AddSingleton(Options.Create(options));
        services.AddSingleton(Options.Create(new MemorySeedOptions { TenantId = "local", Owner = "owner" }));
        services.AddSingleton<ITriageConfigurationRepository>(new StaticConfigurationRepository(configuration));
        services.AddSingleton<IMemoryRepository>(new InventoryMemoryRepository(inventory));
        services.AddSingleton(LocalOnnxTestArtifacts.Store(handler ?? ScriptedHttpMessageHandler.Refusing()));
        return services.BuildServiceProvider();
    }

    private static MemoryCorpusInventory EmptyCorpus() => new(null, [], 0, 0);

    private sealed record CommandResult(int? ExitCode, string Output, string Error);

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
