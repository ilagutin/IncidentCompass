using System.Text.Json;

namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <summary>
/// Reads and writes the active manifest. A manifest is written to a temporary file in the model
/// directory and renamed over the active one, so a reader sees the old manifest or the new one and
/// never a partial file.
/// </summary>
internal static class LocalOnnxModelManifestSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,

        // Every manifest field is required. A missing field is refused rather than read with the
        // type's default: a manifest without "normalize" would otherwise read as false and quietly
        // produce unnormalized vectors.
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    /// <summary>
    /// Returns <see langword="null" /> when no manifest is installed, and refuses one that is not
    /// valid JSON, lacks any field, uses another schema version, or carries a value the adapter
    /// cannot use.
    /// </summary>
    public static async Task<LocalOnnxModelManifest?> ReadAsync(string manifestPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        LocalOnnxModelManifest? manifest;
        try
        {
            await using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous);
            manifest = await JsonSerializer.DeserializeAsync<LocalOnnxModelManifest>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new LocalOnnxModelStoreException(
                LocalOnnxModelErrorCodes.ManifestInvalid,
                "The installed model manifest is not valid JSON or lacks a required field.",
                exception);
        }

        if (manifest is null || !IsComplete(manifest))
        {
            throw new LocalOnnxModelStoreException(
                LocalOnnxModelErrorCodes.ManifestInvalid,
                "The installed model manifest has a blank or unusable field or an unsupported schema version.");
        }

        return manifest;
    }

    public static async Task WriteAtomicallyAsync(
        string manifestPath,
        LocalOnnxModelManifest manifest,
        CancellationToken cancellationToken)
    {
        var temporaryPath = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            // The manifest is the only file ever moved over an existing one. It is the single switch
            // that says which artifacts are active, and a rename within one directory replaces it
            // atomically; artifact files are only ever renamed to a name that does not exist yet.
            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        catch
        {
            LocalOnnxModelFiles.TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    private static bool IsComplete(LocalOnnxModelManifest manifest) =>
        manifest.SchemaVersion == LocalOnnxModelManifest.CurrentSchemaVersion &&
        !string.IsNullOrWhiteSpace(manifest.Id) &&
        !string.IsNullOrWhiteSpace(manifest.Revision) &&
        IsComplete(manifest.ModelFile, LocalOnnxModelArtifact.OnnxKind) &&
        IsComplete(manifest.TokenizerFile, LocalOnnxModelArtifact.SentencePieceKind) &&
        manifest.Dimensions > 0 &&
        manifest.MaxTokens >= 3 &&
        manifest.QueryPrefix is not null &&
        manifest.PassagePrefix is not null &&
        string.Equals(manifest.Pooling, LocalOnnxEmbeddingOptions.MeanPooling, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(manifest.License);

    private static bool IsComplete(LocalOnnxModelArtifact? artifact, string expectedKind) =>
        artifact is not null &&
        !string.IsNullOrWhiteSpace(artifact.Path) &&
        LocalOnnxModelLayout.IsHttpsUrl(artifact.Url) &&
        LocalOnnxModelLayout.IsSha256Hex(artifact.Sha256) &&
        string.Equals(artifact.Kind, expectedKind, StringComparison.Ordinal);
}
