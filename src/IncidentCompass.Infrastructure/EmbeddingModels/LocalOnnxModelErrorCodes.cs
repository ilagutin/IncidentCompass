namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <summary>
/// The stable codes of a local embedding model that is not usable. The install pass records one of
/// them, and the adapter refuses every call with the recorded code until a pass succeeds.
/// </summary>
internal static class LocalOnnxModelErrorCodes
{
    /// <summary>No install pass has completed on this host.</summary>
    public const string NotInstalled = "embedding_model_not_installed";

    /// <summary>A file's SHA-256 is not the pinned one. The file is never repaired or replaced.</summary>
    public const string DigestMismatch = "embedding_model_digest_mismatch";

    /// <summary>The installed manifest names a file that is not on disk.</summary>
    public const string FileMissing = "embedding_model_file_missing";

    /// <summary>A missing file could not be downloaded; nothing was renamed into place.</summary>
    public const string FetchFailed = "embedding_model_fetch_failed";

    /// <summary>A download declared or delivered more bytes than the configured limit; it was discarded.</summary>
    public const string DownloadTooLarge = "embedding_model_download_too_large";

    /// <summary>The install pass ran past its configured bound.</summary>
    public const string InstallTimedOut = "embedding_model_install_timed_out";

    /// <summary>The installed manifest is unreadable, incomplete or names a path outside the directory.</summary>
    public const string ManifestInvalid = "embedding_model_manifest_invalid";

    /// <summary>The model directory could not be read or written.</summary>
    public const string StoreUnavailable = "embedding_model_store_unavailable";
}
