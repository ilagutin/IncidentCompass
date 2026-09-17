namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <summary>
/// Makes a model directory hold the verified files of one pinned artifact set, or says with a named
/// code why it does not. What the artifacts are for is the pin's business, not the store's: it
/// fetches, verifies and switches the same way for every kind. It has three entry points.
/// <list type="bullet">
/// <item><see cref="EnsureInstalledAsync" /> is the Worker start path. An installed manifest wins:
/// both of its files are verified, and it is never replaced by what the pin now describes. An
/// empty directory is filled to the pinned model.</item>
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
        LocalOnnxModelPin pin,
        CancellationToken cancellationToken) =>
        InModelDirectoryAsync(pin, async modelDirectory =>
        {
            var manifestPath = LocalOnnxModelLayout.GetManifestPath(modelDirectory);
            var installed = await LocalOnnxModelManifestSerializer.ReadAsync(manifestPath, cancellationToken);
            if (installed is not null)
            {
                return await VerifyInstalledAsync(modelDirectory, installed, cancellationToken);
            }

            var manifest = CreateManifest(pin);
            var model = await InstallArtifactsAsync(modelDirectory, manifest, pin, cancellationToken);
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
        LocalOnnxModelPin pin,
        CancellationToken cancellationToken) =>
        InModelDirectoryAsync(pin, async modelDirectory =>
        {
            var manifestPath = LocalOnnxModelLayout.GetManifestPath(modelDirectory);
            var configured = CreateManifest(pin);
            var installed = await ReadReadableManifestAsync(manifestPath, cancellationToken);
            if (installed is not null && DescribesTheSameModel(installed, configured))
            {
                var verified = await VerifyInstalledAsync(modelDirectory, installed, cancellationToken);
                return new LocalOnnxModelInstallResult(verified, ManifestSwitched: false, PreviousManifestPath: null);
            }

            var model = await InstallArtifactsAsync(modelDirectory, configured, pin, cancellationToken);
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
        LocalOnnxModelPin pin,
        CancellationToken cancellationToken) =>
        InModelDirectoryAsync<LocalOnnxInstalledModel?>(pin, async modelDirectory =>
        {
            var installed = await LocalOnnxModelManifestSerializer.ReadAsync(
                LocalOnnxModelLayout.GetManifestPath(modelDirectory),
                cancellationToken);
            return installed is null
                ? null
                : await VerifyInstalledAsync(modelDirectory, installed, cancellationToken);
        });

    /// <summary>
    /// The manifest an empty model directory is filled to, from the pin. Each artifact is placed
    /// under its own digest, and the settings only one kind of model carries are written only for
    /// that kind.
    /// </summary>
    public static LocalOnnxModelManifest CreateManifest(LocalOnnxModelPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        return new LocalOnnxModelManifest(
            LocalOnnxModelManifest.CurrentSchemaVersion,
            pin.ModelId,
            pin.Revision,
            CreateArtifact(pin.ModelFile),
            CreateArtifact(pin.TokenizerFile),
            pin.MaxTokens,
            pin.License,
            pin.Kind,
            pin.EmbeddingProfile?.Dimensions,
            pin.EmbeddingProfile?.Pooling,
            pin.EmbeddingProfile?.Normalize,
            pin.EmbeddingProfile?.QueryPrefix,
            pin.EmbeddingProfile?.PassagePrefix);
    }

    private static LocalOnnxModelArtifact CreateArtifact(LocalOnnxPinnedArtifact artifact) =>
        new(
            LocalOnnxModelLayout.GetArtifactRelativePath(artifact.Sha256, artifact.Url),
            artifact.Url,
            artifact.Sha256,
            artifact.Kind);

    private static async Task<T> InModelDirectoryAsync<T>(
        LocalOnnxModelPin pin,
        Func<string, Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentException.ThrowIfNullOrWhiteSpace(pin.ModelDirectory);
        var modelDirectory = Path.GetFullPath(pin.ModelDirectory);
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

    /// <summary>
    /// Whether the installed manifest and the configured one describe the same model: the same
    /// identity, the same two artifacts, the same license and the same settings. Only the schema
    /// version is left out of the comparison, so a manifest an older release wrote for exactly this
    /// model is recognised as the active one. Installing over it would fetch nothing and change
    /// nothing an operator can see, while rewriting a file on a volume that may be read-only and
    /// advising a corpus rebuild the installed model does not need.
    /// </summary>
    private static bool DescribesTheSameModel(
        LocalOnnxModelManifest installed,
        LocalOnnxModelManifest configured) =>
        installed with { SchemaVersion = configured.SchemaVersion } == configured;

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
        LocalOnnxModelPin pin,
        CancellationToken cancellationToken)
    {
        LocalOnnxModelTemporaryFileSweeper.RemoveStaleDownloads(
            modelDirectory,
            TimeSpan.FromSeconds(pin.InstallTimeoutSeconds),
            (timeProvider ?? TimeProvider.System).GetUtcNow());
        var modelFilePath = ResolveArtifactPath(modelDirectory, manifest.ModelFile);
        var tokenizerFilePath = ResolveArtifactPath(modelDirectory, manifest.TokenizerFile);
        await EnsureArtifactAsync(manifest.ModelFile, modelFilePath, pin.MaxDownloadBytes, cancellationToken);
        await EnsureArtifactAsync(manifest.TokenizerFile, tokenizerFilePath, pin.MaxDownloadBytes, cancellationToken);
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
