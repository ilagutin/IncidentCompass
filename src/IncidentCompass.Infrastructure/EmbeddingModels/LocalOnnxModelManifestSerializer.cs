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

        // Every field a manifest of any kind must carry is required here. A missing one is refused
        // rather than read with the type's default: a manifest without "normalize" would otherwise
        // read as false and quietly produce unnormalized vectors. The fields only one kind carries
        // are nullable instead, and IsComplete refuses a kind that lacks one of its own.
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    /// <summary>
    /// Returns <see langword="null" /> when no manifest is installed, and refuses one that is not
    /// valid JSON, lacks a field its kind calls for, names no kind this version knows, uses an
    /// unsupported schema version, or carries a value the adapter cannot use.
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

        // A schema 1 manifest predates the kind field and could only ever describe an embedding
        // model, so it is read as that kind. A shipped release wrote those manifests onto operator
        // volumes; refusing one here would close the embedding path on every host that upgrades.
        manifest = manifest is { SchemaVersion: LocalOnnxModelManifest.LegacyEmbeddingSchemaVersion, Kind: null }
            ? manifest with { Kind = LocalOnnxModelManifest.EmbeddingKind }
            : manifest;

        if (manifest is null || !IsComplete(manifest))
        {
            throw new LocalOnnxModelStoreException(
                LocalOnnxModelErrorCodes.ManifestInvalid,
                "The installed model manifest has a blank or unusable field or an unsupported schema version.");
        }

        return manifest;
    }

    /// <summary>
    /// Writes the active manifest, refusing one this version could not read back before anything is
    /// created. A manifest that fails the same check <see cref="ReadAsync" /> applies would install
    /// a model directory that refuses itself on the next read, so it is refused here, where the
    /// caller still has a model directory in the state it was in.
    /// </summary>
    public static async Task WriteAtomicallyAsync(
        string manifestPath,
        LocalOnnxModelManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!IsComplete(manifest))
        {
            throw new LocalOnnxModelStoreException(
                LocalOnnxModelErrorCodes.ManifestInvalid,
                $"The manifest of {manifest.Id} is of kind '{manifest.Kind}' and lacks a field that kind" +
                " requires, so it was not written; the model directory was left as it was.");
        }

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

    /// <summary>
    /// Copies a manifest byte for byte to <paramref name="destinationPath" /> through a temporary file
    /// and a rename, so a kept copy is never partial. The bytes are copied rather than re-serialized,
    /// so a manifest this version cannot read is kept exactly as it was.
    /// </summary>
    public static async Task CopyAtomicallyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var source = new FileStream(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             4096,
                             FileOptions.Asynchronous))
            {
                await using var destination = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous);
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
                destination.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        catch
        {
            LocalOnnxModelFiles.TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    /// <summary>
    /// What every manifest must carry, whatever it describes. The schema is this version's or the
    /// embedding-only one it replaced, and both files are named, addressed and digested.
    /// </summary>
    private static bool IsComplete(LocalOnnxModelManifest manifest) =>
        manifest.SchemaVersion is LocalOnnxModelManifest.CurrentSchemaVersion
            or LocalOnnxModelManifest.LegacyEmbeddingSchemaVersion &&
        !string.IsNullOrWhiteSpace(manifest.Id) &&
        !string.IsNullOrWhiteSpace(manifest.Revision) &&
        !string.IsNullOrWhiteSpace(manifest.License) &&
        manifest.MaxTokens >= 3 &&
        IsComplete(manifest.ModelFile, LocalOnnxModelArtifact.OnnxKind) &&
        IsKindComplete(manifest);

    /// <summary>
    /// What one kind of manifest must carry beyond that. An embedding manifest keeps every rule it
    /// had: the SentencePiece tokenizer, mean pooling, a vector width and both prefixes. A relevance
    /// judge names a tokenizer of its own kind and nothing embedding-specific, and exists only in
    /// this version's schema. An unknown or absent kind is refused.
    /// </summary>
    private static bool IsKindComplete(LocalOnnxModelManifest manifest) => manifest.Kind switch
    {
        LocalOnnxModelManifest.EmbeddingKind =>
            IsComplete(manifest.TokenizerFile, LocalOnnxModelArtifact.SentencePieceKind) &&
            manifest.Dimensions > 0 &&
            manifest.Normalize is not null &&
            manifest.QueryPrefix is not null &&
            manifest.PassagePrefix is not null &&
            string.Equals(manifest.Pooling, LocalOnnxEmbeddingOptions.MeanPooling, StringComparison.Ordinal),
        LocalOnnxModelManifest.RelevanceJudgeKind =>
            manifest.SchemaVersion == LocalOnnxModelManifest.CurrentSchemaVersion &&
            IsComplete(manifest.TokenizerFile, expectedKind: null),
        _ => false
    };

    private static bool IsComplete(LocalOnnxModelArtifact? artifact, string? expectedKind) =>
        artifact is not null &&
        !string.IsNullOrWhiteSpace(artifact.Path) &&
        LocalOnnxModelLayout.IsHttpsUrl(artifact.Url) &&
        LocalOnnxModelLayout.IsSha256Hex(artifact.Sha256) &&
        (expectedKind is null
            ? !string.IsNullOrWhiteSpace(artifact.Kind)
            : string.Equals(artifact.Kind, expectedKind, StringComparison.Ordinal));
}
