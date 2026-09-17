using System.Net;
using IncidentCompass.Infrastructure.EmbeddingModels;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The model store against real files on disk and a scripted HTTP handler: what is fetched, what is
/// verified, what is refused with which code, and that a refused or failed install leaves no final
/// file, no temporary file and no manifest behind.
/// </summary>
public sealed class LocalOnnxModelStoreTests : IDisposable
{
    private readonly LocalOnnxTestDirectory directory = new();

    public void Dispose() => directory.Dispose();

    [Fact]
    public async Task EnsureInstalled_FetchesAndVerifiesBothFilesAndWritesTheManifestLast()
    {
        var manifestExistedDuringFetch = false;
        using var handler = new ScriptedHttpMessageHandler((request, _) =>
        {
            manifestExistedDuringFetch |= File.Exists(ManifestPath);
            var body = request.RequestUri!.AbsoluteUri == LocalOnnxTestArtifacts.ModelUrl
                ? LocalOnnxTestArtifacts.ModelBytes
                : LocalOnnxTestArtifacts.TokenizerBytes;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        });
        var pin = Pin();

        var installed = await LocalOnnxTestArtifacts.Store(handler).EnsureInstalledAsync(pin, TestContext.Current.CancellationToken);

        Assert.False(manifestExistedDuringFetch);
        Assert.Equal(2, handler.RequestedUris.Count);
        Assert.Equal(ArtifactPath(LocalOnnxTestArtifacts.ModelUrl), installed.ModelFilePath);
        Assert.Equal(ArtifactPath(LocalOnnxTestArtifacts.TokenizerUrl), installed.TokenizerFilePath);
        Assert.NotEqual(Path.GetDirectoryName(installed.ModelFilePath), Path.GetDirectoryName(installed.TokenizerFilePath));
        Assert.Equal(LocalOnnxTestArtifacts.ModelBytes, await File.ReadAllBytesAsync(installed.ModelFilePath, TestContext.Current.CancellationToken));
        Assert.Equal(LocalOnnxTestArtifacts.TokenizerBytes, await File.ReadAllBytesAsync(installed.TokenizerFilePath, TestContext.Current.CancellationToken));
        Assert.Equal(LocalOnnxModelStore.CreateManifest(pin), installed.Manifest);
        Assert.Equal(installed.Manifest, await ReadManifestAsync());
        Assert.Empty(LeftoverTemporaryFiles());
    }

    [Fact]
    public void CreateManifest_KeysEachArtifactUnderItsOwnDigest()
    {
        var manifest = LocalOnnxModelStore.CreateManifest(Pin());

        Assert.Equal(
            "artifacts/" + LocalOnnxTestArtifacts.Sha256(LocalOnnxTestArtifacts.ModelBytes) + "/model.onnx",
            manifest.ModelFile.Path);
        Assert.Equal(
            "artifacts/" + LocalOnnxTestArtifacts.Sha256(LocalOnnxTestArtifacts.TokenizerBytes) + "/sentencepiece.bpe.model",
            manifest.TokenizerFile.Path);
    }

    [Fact]
    public async Task EnsureInstalled_FollowsAnHttpsRedirect()
    {
        using var handler = new ScriptedHttpMessageHandler((request, _) => Task.FromResult(request.RequestUri!.AbsoluteUri switch
        {
            LocalOnnxTestArtifacts.ModelUrl => Redirect("https://cdn.example/blobs/model?signature=abc"),
            "https://cdn.example/blobs/model?signature=abc" => Ok(LocalOnnxTestArtifacts.ModelBytes),
            _ => Ok(LocalOnnxTestArtifacts.TokenizerBytes)
        }));

        var installed = await LocalOnnxTestArtifacts.Store(handler).EnsureInstalledAsync(Pin(), TestContext.Current.CancellationToken);

        Assert.Equal(3, handler.RequestedUris.Count);
        Assert.Equal(LocalOnnxTestArtifacts.ModelBytes, await File.ReadAllBytesAsync(installed.ModelFilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnsureInstalled_RefusesARedirectToPlainHttp()
    {
        using var handler = new ScriptedHttpMessageHandler((_, _) =>
            Task.FromResult(Redirect("http://cdn.example/blobs/model")));

        var exception = await InstallExpectingFailureAsync(handler);

        Assert.Equal(LocalOnnxModelErrorCodes.FetchFailed, exception.ErrorCode);
        Assert.Single(handler.RequestedUris);
        AssertNothingInstalled();
    }

    [Fact]
    public async Task EnsureInstalled_RefusesADownloadWithTheWrongDigest()
    {
        var wrongBytes = LocalOnnxTestArtifacts.CreateBytes(4096, seed: 99);
        using var handler = ScriptedHttpMessageHandler.Serving(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [LocalOnnxTestArtifacts.ModelUrl] = wrongBytes,
            [LocalOnnxTestArtifacts.TokenizerUrl] = LocalOnnxTestArtifacts.TokenizerBytes
        });

        var exception = await InstallExpectingFailureAsync(handler);

        Assert.Equal(LocalOnnxModelErrorCodes.DigestMismatch, exception.ErrorCode);
        Assert.Contains(LocalOnnxTestArtifacts.Sha256(wrongBytes), exception.Message, StringComparison.Ordinal);
        AssertNothingInstalled();
    }

    [Fact]
    public async Task EnsureInstalled_RefusesADownloadThatDeclaresMoreThanTheLimit()
    {
        using var handler = LocalOnnxTestArtifacts.ServingBoth();

        var exception = await InstallExpectingFailureAsync(handler, Pin(maxDownloadBytes: 1000));

        Assert.Equal(LocalOnnxModelErrorCodes.DownloadTooLarge, exception.ErrorCode);
        Assert.Contains("declares 4096 bytes", exception.Message, StringComparison.Ordinal);
        AssertNothingInstalled();
    }

    [Fact]
    public async Task EnsureInstalled_StopsADownloadThatRunsPastTheLimitWithoutDeclaringIt()
    {
        using var handler = new ScriptedHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new UndeclaredLengthStream(LocalOnnxTestArtifacts.ModelBytes))
        }));

        var exception = await InstallExpectingFailureAsync(handler, Pin(maxDownloadBytes: 1000));

        Assert.Equal(LocalOnnxModelErrorCodes.DownloadTooLarge, exception.ErrorCode);
        Assert.Contains("ran past", exception.Message, StringComparison.Ordinal);
        AssertNothingInstalled();
    }

    [Fact]
    public async Task EnsureInstalled_WhenTheStreamFailsMidDownload_LeavesNoFileBehind()
    {
        using var handler = new ScriptedHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FailingStream(LocalOnnxTestArtifacts.ModelBytes, failAfterBytes: 1000))
        }));

        var exception = await InstallExpectingFailureAsync(handler);

        Assert.Equal(LocalOnnxModelErrorCodes.FetchFailed, exception.ErrorCode);
        AssertNothingInstalled();
    }

    [Fact]
    public async Task EnsureInstalled_RefusesAnErrorStatusNamingIt()
    {
        using var handler = new ScriptedHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var exception = await InstallExpectingFailureAsync(handler);

        Assert.Equal(LocalOnnxModelErrorCodes.FetchFailed, exception.ErrorCode);
        Assert.Contains("HTTP 503", exception.Message, StringComparison.Ordinal);
        AssertNothingInstalled();
    }

    [Fact]
    public async Task EnsureInstalled_VerifiesValidPreplacedFilesWithoutFetching()
    {
        await PlaceAsync(LocalOnnxTestArtifacts.ModelUrl, LocalOnnxTestArtifacts.ModelBytes);
        await PlaceAsync(LocalOnnxTestArtifacts.TokenizerUrl, LocalOnnxTestArtifacts.TokenizerBytes);
        using var handler = ScriptedHttpMessageHandler.Refusing();

        var installed = await LocalOnnxTestArtifacts.Store(handler).EnsureInstalledAsync(Pin(), TestContext.Current.CancellationToken);

        Assert.Empty(handler.RequestedUris);
        Assert.Equal(installed.Manifest, await ReadManifestAsync());
    }

    /// <summary>
    /// The store installs a pinned artifact set that is not an embedding model through the same one
    /// path: files already in place are verified and never fetched, and the manifest it writes names
    /// the kind and carries none of the embedding settings.
    /// </summary>
    [Fact]
    public async Task EnsureInstalled_OnAPinThatIsNotAnEmbeddingPin_VerifiesPreplacedFilesAndWritesItsKind()
    {
        await PlaceAsync(LocalOnnxTestArtifacts.ModelUrl, LocalOnnxTestArtifacts.ModelBytes);
        await PlaceAsync(LocalOnnxTestArtifacts.TokenizerUrl, LocalOnnxTestArtifacts.TokenizerBytes);
        using var handler = ScriptedHttpMessageHandler.Refusing();

        var installed = await LocalOnnxTestArtifacts.Store(handler)
            .EnsureInstalledAsync(JudgePin(), TestContext.Current.CancellationToken);

        Assert.Empty(handler.RequestedUris);
        Assert.Equal(LocalOnnxModelManifest.RelevanceJudgeKind, installed.Manifest.Kind);
        Assert.Equal(LocalOnnxModelManifest.CurrentSchemaVersion, installed.Manifest.SchemaVersion);
        Assert.Null(installed.Manifest.Dimensions);
        Assert.Equal(installed.Manifest, await ReadManifestAsync());
        Assert.Empty(LeftoverTemporaryFiles());
    }

    [Fact]
    public async Task EnsureInstalled_OnAPinThatIsNotAnEmbeddingPin_RefusesAWrongDigestWithTheSameCode()
    {
        var operatorBytes = LocalOnnxTestArtifacts.CreateBytes(2048, seed: 31);
        await PlaceAsync(LocalOnnxTestArtifacts.ModelUrl, operatorBytes);
        using var handler = ScriptedHttpMessageHandler.Refusing();

        var exception = await InstallExpectingFailureAsync(handler, JudgePin());

        Assert.Equal(LocalOnnxModelErrorCodes.DigestMismatch, exception.ErrorCode);
        Assert.Empty(handler.RequestedUris);
        Assert.False(File.Exists(ManifestPath));
    }

    [Fact]
    public async Task EnsureInstalled_RefusesAnInvalidPreplacedFileAndLeavesItUntouched()
    {
        var operatorBytes = LocalOnnxTestArtifacts.CreateBytes(2048, seed: 7);
        await PlaceAsync(LocalOnnxTestArtifacts.ModelUrl, operatorBytes);
        using var handler = LocalOnnxTestArtifacts.ServingBoth();

        var exception = await InstallExpectingFailureAsync(handler);

        Assert.Equal(LocalOnnxModelErrorCodes.DigestMismatch, exception.ErrorCode);
        Assert.Empty(handler.RequestedUris);
        Assert.Equal(operatorBytes, await File.ReadAllBytesAsync(ArtifactPath(LocalOnnxTestArtifacts.ModelUrl), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(ManifestPath));
    }

    [Fact]
    public async Task EnsureInstalled_RefusesAnInstalledModelWhoseFileWasCorrupted()
    {
        using (var installHandler = LocalOnnxTestArtifacts.ServingBoth())
        {
            await LocalOnnxTestArtifacts.Store(installHandler).EnsureInstalledAsync(Pin(), TestContext.Current.CancellationToken);
        }

        var manifestBytes = await File.ReadAllBytesAsync(ManifestPath, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(ArtifactPath(LocalOnnxTestArtifacts.ModelUrl), LocalOnnxTestArtifacts.CreateBytes(4096, seed: 5), TestContext.Current.CancellationToken);
        using var handler = ScriptedHttpMessageHandler.Refusing();

        var exception = await InstallExpectingFailureAsync(handler);

        Assert.Equal(LocalOnnxModelErrorCodes.DigestMismatch, exception.ErrorCode);
        Assert.Empty(handler.RequestedUris);
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(ManifestPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnsureInstalled_RefusesAnInstalledManifestNamingAMissingFile()
    {
        using (var installHandler = LocalOnnxTestArtifacts.ServingBoth())
        {
            await LocalOnnxTestArtifacts.Store(installHandler).EnsureInstalledAsync(Pin(), TestContext.Current.CancellationToken);
        }

        File.Delete(ArtifactPath(LocalOnnxTestArtifacts.TokenizerUrl));
        using var handler = ScriptedHttpMessageHandler.Refusing();

        var exception = await InstallExpectingFailureAsync(handler);

        Assert.Equal(LocalOnnxModelErrorCodes.FileMissing, exception.ErrorCode);
    }

    /// <summary>
    /// A host whose options now describe another model keeps the model it has installed: the store
    /// verifies and returns the installed manifest and fetches nothing. Reporting the difference is
    /// not the store's job.
    /// </summary>
    [Fact]
    public async Task EnsureInstalled_KeepsTheInstalledManifestWhenTheOptionsDescribeAnotherModel()
    {
        using (var installHandler = LocalOnnxTestArtifacts.ServingBoth())
        {
            await LocalOnnxTestArtifacts.Store(installHandler).EnsureInstalledAsync(Pin(), TestContext.Current.CancellationToken);
        }

        var manifestBytes = await File.ReadAllBytesAsync(ManifestPath, TestContext.Current.CancellationToken);
        var otherModel = new LocalOnnxEmbeddingOptions
        {
            ModelDirectory = directory.FullPath,
            ModelId = "test/other-model",
            ModelFileUrl = "https://models.example/org/other/resolve/rev-2/onnx/other.onnx",
            ModelFileSha256 = new string('a', 64)
        };
        using var handler = ScriptedHttpMessageHandler.Refusing();

        var installed = await LocalOnnxTestArtifacts.Store(handler).EnsureInstalledAsync(otherModel.CreatePin(), TestContext.Current.CancellationToken);

        Assert.Equal("test/model", installed.Manifest.Id);
        Assert.Empty(handler.RequestedUris);
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(ManifestPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnsureInstalled_RefusesAManifestPathThatLeavesTheModelDirectory()
    {
        var manifest = LocalOnnxModelStore.CreateManifest(Pin());
        await LocalOnnxModelManifestSerializer.WriteAtomicallyAsync(
            ManifestPath,
            manifest with { ModelFile = manifest.ModelFile with { Path = "../outside/model.onnx" } },
            TestContext.Current.CancellationToken);
        using var handler = ScriptedHttpMessageHandler.Refusing();

        var exception = await InstallExpectingFailureAsync(handler);

        Assert.Equal(LocalOnnxModelErrorCodes.ManifestInvalid, exception.ErrorCode);
    }

    [Fact]
    public async Task EnsureInstalled_RefusesAnUnreadableManifest()
    {
        await File.WriteAllTextAsync(ManifestPath, "not a manifest", TestContext.Current.CancellationToken);
        using var handler = ScriptedHttpMessageHandler.Refusing();

        var exception = await InstallExpectingFailureAsync(handler);

        Assert.Equal(LocalOnnxModelErrorCodes.ManifestInvalid, exception.ErrorCode);
    }

    [Fact]
    public async Task EnsureInstalled_WhenCancelledMidDownload_LeavesNoFileBehind()
    {
        using var handler = new ScriptedHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream())
        }));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LocalOnnxTestArtifacts.Store(handler).EnsureInstalledAsync(Pin(), cancellation.Token));

        AssertNothingInstalled();
    }

    private string ManifestPath => LocalOnnxModelLayout.GetManifestPath(directory.FullPath);

    private LocalOnnxModelPin Pin(long maxDownloadBytes = LocalOnnxEmbeddingOptions.DefaultMaxDownloadBytes) =>
        LocalOnnxTestArtifacts.Options(directory.FullPath, maxDownloadBytes).CreatePin();

    private LocalOnnxModelPin JudgePin() => LocalOnnxTestArtifacts.JudgePin(directory.FullPath);

    private string ArtifactPath(string url)
    {
        var bytes = url == LocalOnnxTestArtifacts.ModelUrl
            ? LocalOnnxTestArtifacts.ModelBytes
            : LocalOnnxTestArtifacts.TokenizerBytes;
        return Path.GetFullPath(Path.Combine(
            directory.FullPath,
            LocalOnnxModelLayout.GetArtifactRelativePath(LocalOnnxTestArtifacts.Sha256(bytes), url)));
    }

    private async Task PlaceAsync(string url, byte[] bytes)
    {
        var path = ArtifactPath(url);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
    }

    private async Task<LocalOnnxModelStoreException> InstallExpectingFailureAsync(
        HttpMessageHandler handler,
        LocalOnnxModelPin? pin = null) =>
        await Assert.ThrowsAsync<LocalOnnxModelStoreException>(() =>
            LocalOnnxTestArtifacts.Store(handler).EnsureInstalledAsync(pin ?? Pin(), TestContext.Current.CancellationToken));

    private async Task<LocalOnnxModelManifest?> ReadManifestAsync() =>
        await LocalOnnxModelManifestSerializer.ReadAsync(ManifestPath, TestContext.Current.CancellationToken);

    private void AssertNothingInstalled()
    {
        Assert.False(File.Exists(ArtifactPath(LocalOnnxTestArtifacts.ModelUrl)));
        Assert.False(File.Exists(ManifestPath));
        Assert.Empty(LeftoverTemporaryFiles());
    }

    private string[] LeftoverTemporaryFiles() =>
        Directory.EnumerateFiles(directory.FullPath, "*", SearchOption.AllDirectories)
            .Where(static path => path.EndsWith(".partial", StringComparison.Ordinal) ||
                                  path.EndsWith(".tmp", StringComparison.Ordinal))
            .ToArray();

    private static HttpResponseMessage Ok(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private sealed class FailingStream(byte[] content, int failAfterBytes) : MemoryStream(content, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= failAfterBytes)
            {
                throw new IOException("The connection was reset.");
            }

            var allowed = buffer[..(int)Math.Min(buffer.Length, failAfterBytes - Position)];
            return await base.ReadAsync(allowed, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>A body whose length is not declared: a non-seekable stream gives no Content-Length.</summary>
    private sealed class UndeclaredLengthStream(byte[] content) : MemoryStream(content, writable: false)
    {
        public override bool CanSeek => false;
    }

    private sealed class StallingStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
