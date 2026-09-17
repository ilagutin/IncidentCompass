using System.Net;
using System.Text.Json.Nodes;
using IncidentCompass.Infrastructure.EmbeddingModels;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The operator install beside an installed model, a download that loses a race to another installer,
/// and the removal of abandoned downloads.
/// </summary>
public sealed class LocalOnnxModelStoreInstallTests : IDisposable
{
    private const string SecondModelUrl = "https://models.example/org/model/resolve/rev-2/onnx/model-v2.onnx";

    private static readonly byte[] SecondModelBytes = LocalOnnxTestArtifacts.CreateBytes(3072, seed: 11);

    private readonly LocalOnnxTestDirectory directory = new();

    public void Dispose() => directory.Dispose();

    [Fact]
    public async Task InstallConfigured_OnAnEmptyDirectory_InstallsAndSwitchesWithNoPreviousManifest()
    {
        using var handler = LocalOnnxTestArtifacts.ServingBoth();

        var result = await Store(handler).InstallConfiguredAsync(FirstPin(), TestContext.Current.CancellationToken);

        Assert.True(result.ManifestSwitched);
        Assert.Null(result.PreviousManifestPath);
        Assert.Equal(result.Model.Manifest, await ReadManifestAsync(ManifestPath));
        Assert.False(File.Exists(PreviousManifestPath));
    }

    [Fact]
    public async Task InstallConfigured_BesideAnInstalledModel_KeepsTheOldFilesAndThePreviousManifest()
    {
        var first = await InstallFirstAsync();
        using var handler = ScriptedHttpMessageHandler.Serving(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [SecondModelUrl] = SecondModelBytes
        });

        var result = await Store(handler).InstallConfiguredAsync(SecondPin(), TestContext.Current.CancellationToken);

        Assert.True(result.ManifestSwitched);
        Assert.Equal(PreviousManifestPath, result.PreviousManifestPath);
        Assert.Equal("test/model-v2", (await ReadManifestAsync(ManifestPath))!.Id);
        Assert.Equal(first.Manifest, await ReadManifestAsync(PreviousManifestPath));
        Assert.True(File.Exists(first.ModelFilePath), "The previously installed model file was removed.");
        Assert.NotEqual(first.ModelFilePath, result.Model.ModelFilePath);
        Assert.Equal(first.TokenizerFilePath, result.Model.TokenizerFilePath);
        Assert.Equal([new Uri(SecondModelUrl)], handler.RequestedUris);
    }

    [Fact]
    public async Task InstallConfigured_WhenTheConfiguredModelIsActive_OnlyVerifiesIt()
    {
        await InstallFirstAsync();
        var manifestBytes = await File.ReadAllBytesAsync(ManifestPath, TestContext.Current.CancellationToken);
        using var handler = ScriptedHttpMessageHandler.Refusing();

        var result = await Store(handler).InstallConfiguredAsync(FirstPin(), TestContext.Current.CancellationToken);

        Assert.False(result.ManifestSwitched);
        Assert.Null(result.PreviousManifestPath);
        Assert.Empty(handler.RequestedUris);
        Assert.False(File.Exists(PreviousManifestPath));
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(ManifestPath, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A manifest an older release wrote for exactly this model is the active model, not another
    /// one. The install verifies it and stops there: nothing is fetched, the file is left as it is,
    /// no previous manifest is kept, and an operator is not told to rebuild a corpus that already
    /// matches. Upgrading IncidentCompass is not a reason to rewrite an operator's manifest, and on
    /// a read-only model volume writing one would fail an install that has nothing to do.
    /// </summary>
    [Fact]
    public async Task InstallConfigured_WhenTheOlderSchemaDescribesTheConfiguredModel_OnlyVerifiesIt()
    {
        await InstallFirstAsync();
        await RewriteActiveManifestAsTheOlderSchemaAsync();
        var manifestBytes = await File.ReadAllBytesAsync(ManifestPath, TestContext.Current.CancellationToken);
        using var handler = ScriptedHttpMessageHandler.Refusing();

        var result = await Store(handler).InstallConfiguredAsync(FirstPin(), TestContext.Current.CancellationToken);

        Assert.False(result.ManifestSwitched);
        Assert.Null(result.PreviousManifestPath);
        Assert.Empty(handler.RequestedUris);
        Assert.False(File.Exists(PreviousManifestPath));
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(ManifestPath, TestContext.Current.CancellationToken));
        Assert.Equal(LocalOnnxModelManifest.LegacyEmbeddingSchemaVersion, result.Model.Manifest.SchemaVersion);
    }

    /// <summary>
    /// The older schema is not a licence to keep whatever is installed: a manifest of that schema
    /// describing another model is replaced, and kept for rollback, exactly as one of this schema is.
    /// </summary>
    [Fact]
    public async Task InstallConfigured_WhenTheOlderSchemaDescribesAnotherModel_InstallsAndSwitches()
    {
        await InstallFirstAsync();
        await RewriteActiveManifestAsTheOlderSchemaAsync();
        using var handler = ScriptedHttpMessageHandler.Serving(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [SecondModelUrl] = SecondModelBytes
        });

        var result = await Store(handler).InstallConfiguredAsync(SecondPin(), TestContext.Current.CancellationToken);

        Assert.True(result.ManifestSwitched);
        Assert.Equal(PreviousManifestPath, result.PreviousManifestPath);
        Assert.Equal("test/model-v2", (await ReadManifestAsync(ManifestPath))!.Id);
        Assert.Equal(
            LocalOnnxModelManifest.CurrentSchemaVersion,
            (await ReadManifestAsync(ManifestPath))!.SchemaVersion);
        Assert.Equal(
            LocalOnnxModelManifest.LegacyEmbeddingSchemaVersion,
            (await ReadManifestAsync(PreviousManifestPath))!.SchemaVersion);
    }

    [Fact]
    public async Task InstallConfigured_WhenTheActiveModelFileWasCorrupted_RefusesTheNoOp()
    {
        var first = await InstallFirstAsync();
        await File.WriteAllBytesAsync(first.ModelFilePath, LocalOnnxTestArtifacts.CreateBytes(4096, seed: 3), TestContext.Current.CancellationToken);
        using var handler = ScriptedHttpMessageHandler.Refusing();

        var exception = await Assert.ThrowsAsync<LocalOnnxModelStoreException>(() =>
            Store(handler).InstallConfiguredAsync(FirstPin(), TestContext.Current.CancellationToken));

        Assert.Equal(LocalOnnxModelErrorCodes.DigestMismatch, exception.ErrorCode);
    }

    [Fact]
    public async Task InstallConfigured_WhenANewFileFailsVerification_LeavesTheActiveModelInCharge()
    {
        await InstallFirstAsync();
        var manifestBytes = await File.ReadAllBytesAsync(ManifestPath, TestContext.Current.CancellationToken);
        using var handler = ScriptedHttpMessageHandler.Serving(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [SecondModelUrl] = LocalOnnxTestArtifacts.CreateBytes(3072, seed: 12)
        });

        var exception = await Assert.ThrowsAsync<LocalOnnxModelStoreException>(() =>
            Store(handler).InstallConfiguredAsync(SecondPin(), TestContext.Current.CancellationToken));

        Assert.Equal(LocalOnnxModelErrorCodes.DigestMismatch, exception.ErrorCode);
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(ManifestPath, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(PreviousManifestPath));
    }

    [Fact]
    public async Task InstallConfigured_NeverFetchesOverAFileAtItsPath()
    {
        await InstallFirstAsync();
        var operatorBytes = LocalOnnxTestArtifacts.CreateBytes(1000, seed: 13);
        var secondModelPath = ArtifactPath(LocalOnnxTestArtifacts.Sha256(SecondModelBytes), SecondModelUrl);
        Directory.CreateDirectory(Path.GetDirectoryName(secondModelPath)!);
        await File.WriteAllBytesAsync(secondModelPath, operatorBytes, TestContext.Current.CancellationToken);
        using var handler = ScriptedHttpMessageHandler.Refusing();

        var exception = await Assert.ThrowsAsync<LocalOnnxModelStoreException>(() =>
            Store(handler).InstallConfiguredAsync(SecondPin(), TestContext.Current.CancellationToken));

        Assert.Equal(LocalOnnxModelErrorCodes.DigestMismatch, exception.ErrorCode);
        Assert.Empty(handler.RequestedUris);
        Assert.Equal(operatorBytes, await File.ReadAllBytesAsync(secondModelPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadInstalled_ReturnsNullWhenNothingIsInstalledAndTheVerifiedModelOtherwise()
    {
        using var refusing = ScriptedHttpMessageHandler.Refusing();
        Assert.Null(await Store(refusing).ReadInstalledAsync(FirstPin(), TestContext.Current.CancellationToken));

        var first = await InstallFirstAsync();

        Assert.Equal(first, await Store(refusing).ReadInstalledAsync(SecondPin(), TestContext.Current.CancellationToken));
        Assert.Empty(refusing.RequestedUris);
    }

    [Fact]
    public async Task Fetch_WhenAnotherInstallerPlacesTheSameFileFirst_AcceptsIt()
    {
        var modelPath = ArtifactPath(LocalOnnxTestArtifacts.Sha256(LocalOnnxTestArtifacts.ModelBytes), LocalOnnxTestArtifacts.ModelUrl);
        using var handler = RacingHandler(modelPath, LocalOnnxTestArtifacts.ModelBytes);

        var installed = await Store(handler).EnsureInstalledAsync(FirstPin(), TestContext.Current.CancellationToken);

        Assert.Equal(LocalOnnxTestArtifacts.ModelBytes, await File.ReadAllBytesAsync(installed.ModelFilePath, TestContext.Current.CancellationToken));
        Assert.Empty(TemporaryDownloads());
        Assert.True(File.Exists(ManifestPath));
    }

    [Fact]
    public async Task Fetch_WhenAnotherInstallerPlacesADifferentFileFirst_RefusesItAndLeavesItInPlace()
    {
        var modelPath = ArtifactPath(LocalOnnxTestArtifacts.Sha256(LocalOnnxTestArtifacts.ModelBytes), LocalOnnxTestArtifacts.ModelUrl);
        var otherBytes = LocalOnnxTestArtifacts.CreateBytes(4096, seed: 21);
        using var handler = RacingHandler(modelPath, otherBytes);

        var exception = await Assert.ThrowsAsync<LocalOnnxModelStoreException>(() =>
            Store(handler).EnsureInstalledAsync(FirstPin(), TestContext.Current.CancellationToken));

        Assert.Equal(LocalOnnxModelErrorCodes.DigestMismatch, exception.ErrorCode);
        Assert.Contains("appeared while it was being downloaded", exception.Message, StringComparison.Ordinal);
        Assert.Equal(otherBytes, await File.ReadAllBytesAsync(modelPath, TestContext.Current.CancellationToken));
        Assert.Empty(TemporaryDownloads());
        Assert.False(File.Exists(ManifestPath));
    }

    [Fact]
    public async Task Install_RemovesOnlyAbandonedDownloadsOlderThanTheInstallTimeout()
    {
        var digestDirectory = Path.Combine(directory.FullPath, "artifacts", new string('b', 64));
        var otherDigestDirectory = Path.Combine(directory.FullPath, "artifacts", new string('c', 64));
        var notADigestDirectory = Path.Combine(directory.FullPath, "artifacts", "operator-notes");
        var longAgo = DateTime.UtcNow.AddDays(-2);
        var staleDownload = Place(digestDirectory, "model.onnx." + new string('0', 32) + ".partial", longAgo);
        var staleDownloadElsewhere = Place(otherDigestDirectory, "sentencepiece.bpe.model." + new string('f', 32) + ".partial", longAgo);
        var recentDownload = Place(digestDirectory, "model.onnx." + new string('1', 32) + ".partial", DateTime.UtcNow);
        var unrelatedPartial = Place(digestDirectory, "notes.partial", longAgo);
        var uppercaseToken = Place(digestDirectory, "model.onnx." + new string('A', 32) + ".partial", longAgo);
        var outsideDigestDirectory = Place(notADigestDirectory, "model.onnx." + new string('2', 32) + ".partial", longAgo);
        using var handler = LocalOnnxTestArtifacts.ServingBoth();

        await Store(handler).EnsureInstalledAsync(FirstPin(), TestContext.Current.CancellationToken);

        Assert.False(File.Exists(staleDownload));
        Assert.False(File.Exists(staleDownloadElsewhere));
        Assert.True(File.Exists(recentDownload));
        Assert.True(File.Exists(unrelatedPartial));
        Assert.True(File.Exists(uppercaseToken));
        Assert.True(File.Exists(outsideDigestDirectory));
    }

    [Theory]
    [InlineData("model.onnx.0123456789abcdef0123456789abcdef.partial", true)]
    [InlineData("model.onnx.0123456789ABCDEF0123456789abcdef.partial", false)]
    [InlineData("model.onnx.0123456789abcdef.partial", false)]
    [InlineData(".0123456789abcdef0123456789abcdef.partial", false)]
    [InlineData("model.onnx", false)]
    [InlineData("model.onnx.0123456789abcdef0123456789abcdef.tmp", false)]
    public void IsTemporaryDownloadName_MatchesOnlyTheStoresOwnTemporaryNames(string fileName, bool expected)
    {
        Assert.Equal(expected, LocalOnnxModelLayout.IsTemporaryDownloadName(fileName));
        Assert.True(LocalOnnxModelLayout.IsTemporaryDownloadName(Path.GetFileName(LocalOnnxModelLayout.CreateTemporaryDownloadPath("model.onnx"))));
    }

    private string ManifestPath => LocalOnnxModelLayout.GetManifestPath(directory.FullPath);

    private string PreviousManifestPath => LocalOnnxModelLayout.GetPreviousManifestPath(directory.FullPath);

    private LocalOnnxModelPin FirstPin() => LocalOnnxTestArtifacts.Options(directory.FullPath).CreatePin();

    private LocalOnnxModelPin SecondPin() => new LocalOnnxEmbeddingOptions
    {
        ModelDirectory = directory.FullPath,
        ModelId = "test/model-v2",
        Revision = "rev-2",
        ModelFileUrl = SecondModelUrl,
        ModelFileSha256 = LocalOnnxTestArtifacts.Sha256(SecondModelBytes),
        TokenizerFileUrl = LocalOnnxTestArtifacts.TokenizerUrl,
        TokenizerFileSha256 = LocalOnnxTestArtifacts.Sha256(LocalOnnxTestArtifacts.TokenizerBytes),
        Dimensions = 8,
        MaxTokens = 16,
        License = "Apache-2.0"
    }.CreatePin();

    private static LocalOnnxModelStore Store(HttpMessageHandler handler) => LocalOnnxTestArtifacts.Store(handler);

    private async Task<LocalOnnxInstalledModel> InstallFirstAsync()
    {
        using var handler = LocalOnnxTestArtifacts.ServingBoth();
        return await Store(handler).EnsureInstalledAsync(FirstPin(), TestContext.Current.CancellationToken);
    }

    private string ArtifactPath(string sha256, string url) =>
        Path.GetFullPath(Path.Combine(directory.FullPath, LocalOnnxModelLayout.GetArtifactRelativePath(sha256, url)));

    /// <summary>
    /// Rewrites the active manifest as a shipped release wrote it: the same model, the schema that
    /// version used and no kind field at all, which is the manifest sitting on operator volumes.
    /// </summary>
    private async Task RewriteActiveManifestAsTheOlderSchemaAsync()
    {
        var manifest = JsonNode.Parse(
            await File.ReadAllTextAsync(ManifestPath, TestContext.Current.CancellationToken))!.AsObject();
        manifest["schemaVersion"] = JsonValue.Create(LocalOnnxModelManifest.LegacyEmbeddingSchemaVersion);
        Assert.True(manifest.Remove("kind"), "The written manifest has no field kind.");
        await File.WriteAllTextAsync(ManifestPath, manifest.ToJsonString(), TestContext.Current.CancellationToken);
    }

    private static async Task<LocalOnnxModelManifest?> ReadManifestAsync(string path) =>
        await LocalOnnxModelManifestSerializer.ReadAsync(path, TestContext.Current.CancellationToken);

    /// <summary>
    /// Serves the real files, but places <paramref name="winnerBytes" /> at the model's destination
    /// while the model download is in flight, the way a second installer finishing first would.
    /// </summary>
    private static ScriptedHttpMessageHandler RacingHandler(string modelPath, byte[] winnerBytes) =>
        new((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri == LocalOnnxTestArtifacts.ModelUrl)
            {
                File.WriteAllBytes(modelPath, winnerBytes);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(LocalOnnxTestArtifacts.ModelBytes)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(LocalOnnxTestArtifacts.TokenizerBytes)
            });
        });

    private string[] TemporaryDownloads() =>
        Directory.EnumerateFiles(directory.FullPath, "*.partial", SearchOption.AllDirectories).ToArray();

    private static string Place(string parent, string fileName, DateTime lastWriteUtc)
    {
        Directory.CreateDirectory(parent);
        var path = Path.Combine(parent, fileName);
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }
}
