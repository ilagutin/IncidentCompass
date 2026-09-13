namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <summary>
/// Makes a model directory hold a verified model, or says with a named code why it does not. It has
/// three entry points.
/// <list type="bullet">
/// <item><see cref="EnsureInstalledAsync" /> is the Worker start path. An installed manifest wins:
/// both of its files are verified, and it is never replaced by what the host options now describe. An
/// empty directory is filled to the configured model.</item>
/// <item><see cref="InstallConfiguredAsync" /> is the operator install. It places the configured
/// artifacts beside whatever is installed and then switches the active manifest, keeping the one it
/// replaced for rollback.</item>
/// <item><see cref="ReadInstalledAsync" /> verifies and returns the installed model without
/// installing anything, for status.</item>
/// </list>
/// Every install follows the same rules. An artifact already at its expected path was placed by an
/// operator or left by an earlier install; it is verified, never fetched over and never deleted, even
/// when its digest is wrong. A missing artifact is fetched, bounded in size, verified while it streams
/// and renamed into place. The active manifest is written last, so an install that stops at any
/// earlier point leaves the previously active model, or none, in charge. Artifact directories are
/// never deleted. Infrastructure failures are normalized to
/// <see cref="LocalOnnxModelStoreException" /> here.
/// </summary>
internal sealed class LocalOnnxModelStore(LocalOnnxModelFileFetcher fetcher, TimeProvider? timeProvider = null)
{
    public Task<LocalOnnxInstalledModel> EnsureInstalledAsync(
        LocalOnnxEmbeddingOptions options,
        CancellationToken cancellationToken) =>
        InModelDirectoryAsync(options, async modelDirectory =>
        {
            var manifestPath = LocalOnnxModelLayout.GetManifestPath(modelDirectory);
            var installed = await LocalOnnxModelManifestSerializer.ReadAsync(manifestPath, cancellationToken);
            if (installed is not null)
            {
                return await VerifyInstalledAsync(modelDirectory, installed, cancellationToken);
            }

            var manifest = CreateManifest(options);
            var model = await InstallArtifactsAsync(modelDirectory, manifest, options, cancellationToken);
            await LocalOnnxModelManifestSerializer.WriteAtomicallyAsync(manifestPath, manifest, cancellationToken);
            return model;
        });

    /// <summary>
    /// Installs the configured model beside the installed one and makes it active. When the configured
    /// model is already active this only verifies it. An active manifest that cannot be read does not
    /// stop the install, which is the operator's way to replace it, but it is still kept as the
    /// previous manifest byte for byte.
    /// </summary>
    public Task<LocalOnnxModelInstallResult> InstallConfiguredAsync(
        LocalOnnxEmbeddingOptions options,
        CancellationToken cancellationToken) =>
        InModelDirectoryAsync(options, async modelDirectory =>
        {
            var manifestPath = LocalOnnxModelLayout.GetManifestPath(modelDirectory);
            var configured = CreateManifest(options);
            if (await ReadReadableManifestAsync(manifestPath, cancellationToken) == configured)
            {
                var verified = await VerifyInstalledAsync(modelDirectory, configured, cancellationToken);
                return new LocalOnnxModelInstallResult(verified, ManifestSwitched: false, PreviousManifestPath: null);
            }

            var model = await InstallArtifactsAsync(modelDirectory, configured, options, cancellationToken);
            string? previousManifestPath = null;
            if (File.Exists(manifestPath))
            {
                previousManifestPath = LocalOnnxModelLayout.GetPreviousManifestPath(modelDirectory);
                await LocalOnnxModelManifestSerializer.CopyAtomicallyAsync(
                    manifestPath,
                    previousManifestPath,
                    cancellationToken);
            }

            await LocalOnnxModelManifestSerializer.WriteAtomicallyAsync(manifestPath, configured, cancellationToken);
            return new LocalOnnxModelInstallResult(model, ManifestSwitched: true, previousManifestPath);
        });

    /// <summary>
    /// The installed model with both files verified, or <see langword="null" /> when no manifest is
    /// installed. Installs nothing.
    /// </summary>
    public Task<LocalOnnxInstalledModel?> ReadInstalledAsync(
        LocalOnnxEmbeddingOptions options,
        CancellationToken cancellationToken) =>
        InModelDirectoryAsync<LocalOnnxInstalledModel?>(options, async modelDirectory =>
        {
            var installed = await LocalOnnxModelManifestSerializer.ReadAsync(
                LocalOnnxModelLayout.GetManifestPath(modelDirectory),
                cancellationToken);
            return installed is null
                ? null
                : await VerifyInstalledAsync(modelDirectory, installed, cancellationToken);
        });

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

    private static async Task<T> InModelDirectoryAsync<T>(
        LocalOnnxEmbeddingOptions options,
        Func<string, Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelDirectory);
        var modelDirectory = Path.GetFullPath(options.ModelDirectory);
        try
        {
            Directory.CreateDirectory(modelDirectory);
            return await action(modelDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LocalOnnxModelStoreException(
                LocalOnnxModelErrorCodes.StoreUnavailable,
                $"The model directory could not be read or written ({exception.GetType().Name}).",
                exception);
        }
    }

    private static async Task<LocalOnnxModelManifest?> ReadReadableManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await LocalOnnxModelManifestSerializer.ReadAsync(manifestPath, cancellationToken);
        }
        catch (LocalOnnxModelStoreException exception)
            when (exception.ErrorCode == LocalOnnxModelErrorCodes.ManifestInvalid)
        {
            return null;
        }
    }

    private async Task<LocalOnnxInstalledModel> InstallArtifactsAsync(
        string modelDirectory,
        LocalOnnxModelManifest manifest,
        LocalOnnxEmbeddingOptions options,
        CancellationToken cancellationToken)
    {
        LocalOnnxModelTemporaryFileSweeper.RemoveStaleDownloads(
            modelDirectory,
            TimeSpan.FromSeconds(options.InstallTimeoutSeconds),
            (timeProvider ?? TimeProvider.System).GetUtcNow());
        var modelFilePath = ResolveArtifactPath(modelDirectory, manifest.ModelFile);
        var tokenizerFilePath = ResolveArtifactPath(modelDirectory, manifest.TokenizerFile);
        await EnsureArtifactAsync(manifest.ModelFile, modelFilePath, options.MaxDownloadBytes, cancellationToken);
        await EnsureArtifactAsync(manifest.TokenizerFile, tokenizerFilePath, options.MaxDownloadBytes, cancellationToken);
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
