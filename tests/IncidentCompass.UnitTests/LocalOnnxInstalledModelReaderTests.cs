using IncidentCompass.Infrastructure.EmbeddingModels;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Which installed local model the Worker uses: the install pass's recorded state while the host
/// runs, and the verified store before the host starts, when commands run.
/// </summary>
public sealed class LocalOnnxInstalledModelReaderTests
{
    [Fact]
    public async Task Read_OnAnotherEmbeddingProvider_HasNoLocalModel()
    {
        var reader = LocalModelTestSupport.Reader(
            LocalModelTestSupport.InstalledState(LocalModelTestSupport.ModelId, LocalModelTestSupport.FirstDigest),
            provider: "OpenAiCompatible");

        var lookup = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Null(lookup.Model);
        Assert.Equal(LocalOnnxModelErrorCodes.NotInstalled, lookup.ErrorCode);
    }

    [Fact]
    public async Task Read_AfterASuccessfulInstallPass_ReturnsTheRecordedModel()
    {
        var state = LocalModelTestSupport.InstalledState(LocalModelTestSupport.ModelId, LocalModelTestSupport.FirstDigest);

        var lookup = await LocalModelTestSupport.Reader(state).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Same(state.Snapshot.Model, lookup.Model);
    }

    [Fact]
    public async Task Read_AfterAFailedInstallPass_ReturnsTheRecordedCode()
    {
        var reader = LocalModelTestSupport.Reader(LocalModelTestSupport.FailedState(LocalOnnxModelErrorCodes.FetchFailed));

        var lookup = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Null(lookup.Model);
        Assert.Equal(LocalOnnxModelErrorCodes.FetchFailed, lookup.ErrorCode);
    }

    [Fact]
    public async Task Read_WhileTheInstallPassRuns_IsNotInstalled()
    {
        var state = new LocalOnnxModelInstallState();
        state.RecordInstalling();

        var lookup = await LocalModelTestSupport.Reader(state).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(LocalOnnxModelErrorCodes.NotInstalled, lookup.ErrorCode);
    }

    [Fact]
    public async Task Read_BeforeAnyInstallPassOnAnEmptyDirectory_IsNotInstalled()
    {
        using var directory = new LocalOnnxTestDirectory();

        var lookup = await LocalModelTestSupport.Reader(new LocalOnnxModelInstallState(), directory.FullPath)
            .ReadAsync(TestContext.Current.CancellationToken);

        Assert.Null(lookup.Model);
        Assert.Equal(LocalOnnxModelErrorCodes.NotInstalled, lookup.ErrorCode);
    }

    [Fact]
    public async Task Read_BeforeAnyInstallPass_VerifiesTheStoreAndRecordsTheModel()
    {
        using var fixture = await LocalOnnxFixtureModel.InstallAsync(TestContext.Current.CancellationToken);
        var state = new LocalOnnxModelInstallState();

        var lookup = await LocalModelTestSupport.Reader(state, fixture.Options.ModelDirectory)
            .ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(fixture.Installed, lookup.Model);
        Assert.Equal(LocalOnnxModelInstallStatus.Installed, state.Snapshot.Status);
    }

    [Fact]
    public async Task Read_BeforeAnyInstallPass_ReportsAFileThatNoLongerVerifies()
    {
        using var fixture = await LocalOnnxFixtureModel.InstallAsync(TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(fixture.Installed.ModelFilePath, [1, 2, 3], TestContext.Current.CancellationToken);
        var state = new LocalOnnxModelInstallState();

        var lookup = await LocalModelTestSupport.Reader(state, fixture.Options.ModelDirectory)
            .ReadAsync(TestContext.Current.CancellationToken);

        Assert.Null(lookup.Model);
        Assert.Equal(LocalOnnxModelErrorCodes.DigestMismatch, lookup.ErrorCode);
        Assert.Equal(LocalOnnxModelInstallStatus.NotStarted, state.Snapshot.Status);
    }
}
