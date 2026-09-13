namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <summary>
/// Makes a model directory hold a verified model, or says with a named code why it does not.
/// <list type="bullet">
/// <item>An installed manifest wins. Both of its files are verified against its digests, and it is
/// never replaced by what the host options now describe; a configured model that differs from the
/// installed one is reported elsewhere, not repaired here.</item>
/// <item>With no manifest, each artifact already at its expected path is verified. Such a file was
/// placed by an operator or left by an interrupted install; it is never fetched over and never
/// deleted, even when its digest is wrong.</item>
/// <item>Each artifact still missing is fetched, bounded in size, verified while it streams and
/// renamed into place.</item>
/// <item>The manifest is written last. Until it exists nothing reads the artifacts, so an install
/// that stops at any earlier point leaves no active model behind.</item>
/// </list>
/// Infrastructure failures are normalized to <see cref="LocalOnnxModelStoreException" /> here.
/// </summary>
internal sealed class LocalOnnxModelStore(LocalOnnxModelFileFetcher fetcher)
{
    public async Task<LocalOnnxInstalledModel> EnsureInstalledAsync(
        LocalOnnxEmbeddingOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelDirectory);
        var modelDirectory = Path.GetFullPath(options.ModelDirectory);
        try
        {
            Directory.CreateDirectory(modelDirectory);
            var manifestPath = LocalOnnxModelLayout.GetManifestPath(modelDirectory);
            var installed = await LocalOnnxModelManifestSerializer.ReadAsync(manifestPath, cancellationToken);
            return installed is null
                ? await InstallAsync(
                    modelDirectory,
                    manifestPath,
                    CreateManifest(options),
                    options.MaxDownloadBytes,
                    cancellationToken)
                : await VerifyInstalledAsync(modelDirectory, installed, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LocalOnnxModelStoreException(
                LocalOnnxModelErrorCodes.StoreUnavailable,
                $"The model directory could not be read or written ({exception.GetType().Name}).",
                exception);
        }
    }

    /// <summary>
    /// The manifest an empty model directory is filled to, from the host options. Each artifact is
    /// placed under its own digest.
    /// </summary>
    public static LocalOnnxModelManifest CreateManifest(LocalOnnxEmbeddingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new LocalOnnxModelManifest(
            LocalOnnxModelManifest.CurrentSchemaVersion,
            options.ModelId,
            options.Revision,
            new LocalOnnxModelArtifact(
                LocalOnnxModelLayout.GetArtifactRelativePath(options.ModelFileSha256, options.ModelFileUrl),
                options.ModelFileUrl,
                options.ModelFileSha256,
                LocalOnnxModelArtifact.OnnxKind),
            new LocalOnnxModelArtifact(
                LocalOnnxModelLayout.GetArtifactRelativePath(options.TokenizerFileSha256, options.TokenizerFileUrl),
                options.TokenizerFileUrl,
                options.TokenizerFileSha256,
                LocalOnnxModelArtifact.SentencePieceKind),
            options.Dimensions,
            options.MaxTokens,
            options.Pooling,
            options.Normalize,
            options.QueryPrefix,
            options.PassagePrefix,
            options.License);
    }

    private async Task<LocalOnnxInstalledModel> InstallAsync(
        string modelDirectory,
        string manifestPath,
        LocalOnnxModelManifest manifest,
        long maxDownloadBytes,
        CancellationToken cancellationToken)
    {
        var modelFilePath = ResolveArtifactPath(modelDirectory, manifest.ModelFile);
        var tokenizerFilePath = ResolveArtifactPath(modelDirectory, manifest.TokenizerFile);
        await EnsureArtifactAsync(manifest.ModelFile, modelFilePath, maxDownloadBytes, cancellationToken);
        await EnsureArtifactAsync(manifest.TokenizerFile, tokenizerFilePath, maxDownloadBytes, cancellationToken);
        await LocalOnnxModelManifestSerializer.WriteAtomicallyAsync(manifestPath, manifest, cancellationToken);
        return new LocalOnnxInstalledModel(manifest, modelFilePath, tokenizerFilePath);
    }

    private async Task EnsureArtifactAsync(
        LocalOnnxModelArtifact artifact,
        string path,
        long maxDownloadBytes,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            await VerifyAsync(artifact, path, cancellationToken);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await fetcher.FetchAsync(artifact, path, maxDownloadBytes, cancellationToken);
    }

    private static async Task<LocalOnnxInstalledModel> VerifyInstalledAsync(
        string modelDirectory,
        LocalOnnxModelManifest manifest,
        CancellationToken cancellationToken)
    {
        var modelFilePath = ResolveArtifactPath(modelDirectory, manifest.ModelFile);
        var tokenizerFilePath = ResolveArtifactPath(modelDirectory, manifest.TokenizerFile);
        await VerifyInstalledArtifactAsync(manifest.ModelFile, modelFilePath, cancellationToken);
        await VerifyInstalledArtifactAsync(manifest.TokenizerFile, tokenizerFilePath, cancellationToken);
        return new LocalOnnxInstalledModel(manifest, modelFilePath, tokenizerFilePath);
    }

    private static async Task VerifyInstalledArtifactAsync(
        LocalOnnxModelArtifact artifact,
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new LocalOnnxModelStoreException(
                LocalOnnxModelErrorCodes.FileMissing,
                $"The installed manifest names the {artifact.Kind} file {artifact.Path}, which does not exist.");
        }

        await VerifyAsync(artifact, path, cancellationToken);
    }

    private static async Task VerifyAsync(
        LocalOnnxModelArtifact artifact,
        string path,
        CancellationToken cancellationToken)
    {
        var actualSha256 = await LocalOnnxModelFiles.ComputeSha256Async(path, cancellationToken);
        if (!string.Equals(actualSha256, artifact.Sha256, StringComparison.Ordinal))
        {
            throw new LocalOnnxModelStoreException(
                LocalOnnxModelErrorCodes.DigestMismatch,
                $"The {artifact.Kind} file {artifact.Path} has SHA-256 {actualSha256}, not {artifact.Sha256};" +
                " it was left in place and not replaced.");
        }
    }

    private static string ResolveArtifactPath(string modelDirectory, LocalOnnxModelArtifact artifact) =>
        LocalOnnxModelLayout.TryResolve(modelDirectory, artifact.Path, out var fullPath)
            ? fullPath
            : throw new LocalOnnxModelStoreException(
                LocalOnnxModelErrorCodes.ManifestInvalid,
                $"The {artifact.Kind} file path {artifact.Path} does not stay inside the model directory.");
}
