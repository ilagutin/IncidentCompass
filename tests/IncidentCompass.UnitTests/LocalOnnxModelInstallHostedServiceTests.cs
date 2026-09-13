using System.Net;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Infrastructure;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.Infrastructure.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The install pass at Worker start: it runs before the memory seed pass, does nothing on another
/// provider, records success or a named failure without failing the host, and stops with the host.
/// </summary>
public sealed class LocalOnnxModelInstallHostedServiceTests : IDisposable
{
    private readonly LocalOnnxTestDirectory directory = new();
    private readonly LocalOnnxModelInstallState state = new();

    public void Dispose() => directory.Dispose();

    [Fact]
    public void AddEmbeddingHost_RegistersTheModelInstallBeforeTheMemorySeedPass()
    {
        var services = new ServiceCollection();
        services.AddEmbeddingHost(new ConfigurationBuilder().Build());

        var hostedServiceTypes = services
            .Where(static descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(static descriptor => descriptor.ImplementationType)
            .ToList();
        var installIndex = hostedServiceTypes.IndexOf(typeof(LocalOnnxModelInstallHostedService));
        var seedIndex = hostedServiceTypes.IndexOf(typeof(MemorySeedHostedService));

        Assert.True(installIndex >= 0, "The model install hosted service is not registered.");
        Assert.True(seedIndex > installIndex, "The memory seed pass would start before the model install.");
    }

    [Fact]
    public async Task StartAsync_OnAnotherEmbeddingProvider_DoesNothing()
    {
        using var handler = ScriptedHttpMessageHandler.Refusing();

        await CreateService(handler, provider: "Mock").StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(LocalOnnxModelInstallStatus.NotStarted, state.Snapshot.Status);
        Assert.Empty(handler.RequestedUris);
        Assert.False(File.Exists(LocalOnnxModelLayout.GetManifestPath(directory.FullPath)));
    }

    [Fact]
    public async Task StartAsync_InstallsTheModelAndRecordsIt()
    {
        using var handler = LocalOnnxTestArtifacts.ServingBoth();

        await CreateService(handler).StartAsync(TestContext.Current.CancellationToken);

        var snapshot = state.Snapshot;
        Assert.Equal(LocalOnnxModelInstallStatus.Installed, snapshot.Status);
        Assert.Equal("test/model", snapshot.Model!.Manifest.Id);
        Assert.Null(snapshot.ErrorCode);
    }

    [Fact]
    public async Task StartAsync_WhenTheFetchFails_RecordsTheCodeAndLetsTheHostStart()
    {
        using var handler = new ScriptedHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await CreateService(handler).StartAsync(TestContext.Current.CancellationToken);

        var snapshot = state.Snapshot;
        Assert.Equal(LocalOnnxModelInstallStatus.Failed, snapshot.Status);
        Assert.Equal(LocalOnnxModelErrorCodes.FetchFailed, snapshot.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Detail));
        Assert.Null(snapshot.Model);
    }

    [Fact]
    public async Task StartAsync_WhenTheInstallRunsPastItsBound_RecordsTheTimeout()
    {
        using var handler = new ScriptedHttpMessageHandler(static async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await CreateService(handler, installTimeoutSeconds: 1).StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(LocalOnnxModelInstallStatus.Failed, state.Snapshot.Status);
        Assert.Equal(LocalOnnxModelErrorCodes.InstallTimedOut, state.Snapshot.ErrorCode);
    }

    [Fact]
    public async Task StartAsync_WhenTheHostStops_StopsTheInstall()
    {
        using var handler = new ScriptedHttpMessageHandler(static async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stopping.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService(handler).StartAsync(stopping.Token));

        Assert.Equal(LocalOnnxModelInstallStatus.Failed, state.Snapshot.Status);
        Assert.Equal(LocalOnnxModelErrorCodes.NotInstalled, state.Snapshot.ErrorCode);
    }

    private LocalOnnxModelInstallHostedService CreateService(
        HttpMessageHandler handler,
        string provider = "LocalOnnx",
        int installTimeoutSeconds = 60)
    {
        var baseOptions = LocalOnnxTestArtifacts.Options(directory.FullPath);
        var options = new LocalOnnxEmbeddingOptions
        {
            ModelDirectory = baseOptions.ModelDirectory,
            ModelId = baseOptions.ModelId,
            Revision = baseOptions.Revision,
            ModelFileUrl = baseOptions.ModelFileUrl,
            ModelFileSha256 = baseOptions.ModelFileSha256,
            TokenizerFileUrl = baseOptions.TokenizerFileUrl,
            TokenizerFileSha256 = baseOptions.TokenizerFileSha256,
            Dimensions = baseOptions.Dimensions,
            MaxTokens = baseOptions.MaxTokens,
            License = baseOptions.License,
            InstallTimeoutSeconds = installTimeoutSeconds
        };

        return new LocalOnnxModelInstallHostedService(
            Microsoft.Extensions.Options.Options.Create(new EmbeddingOptions { Provider = provider }),
            Microsoft.Extensions.Options.Options.Create(options),
            LocalOnnxTestArtifacts.Store(handler),
            state,
            NullLogger<LocalOnnxModelInstallHostedService>.Instance);
    }
}
