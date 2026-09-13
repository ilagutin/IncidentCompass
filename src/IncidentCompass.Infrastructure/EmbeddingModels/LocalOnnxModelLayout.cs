namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <summary>
/// Where the local embedding model's files live inside the model directory.
/// <para>
/// The directory holds one active manifest at its root and each artifact file under
/// <c>artifacts/&lt;that file's SHA-256&gt;/&lt;file name&gt;</c>. Every file is addressed by its own
/// digest, so a file of any other model or tokenizer never shares a path with an installed one. A
/// later install places new files beside the old ones, including an install that keeps the model and
/// changes only the tokenizer, and switches by replacing the manifest alone. The manifest it replaced
/// is kept beside it as <see cref="PreviousManifestFileName" />, so a rollback is restoring that file.
/// </para>
/// </summary>
internal static class LocalOnnxModelLayout
{
    public const string ManifestFileName = "manifest.json";

    public const string PreviousManifestFileName = "manifest.previous.json";

    public const string ArtifactDirectoryName = "artifacts";

    public const string TemporaryDownloadExtension = ".partial";

    private const int TemporaryNameTokenLength = 32;

    public static string GetManifestPath(string modelDirectory) =>
        Path.Combine(modelDirectory, ManifestFileName);

    public static string GetPreviousManifestPath(string modelDirectory) =>
        Path.Combine(modelDirectory, PreviousManifestFileName);

    /// <summary>
    /// The manifest path of an artifact: always relative, always forward slashes, so a manifest
    /// written on one platform reads the same on another.
    /// </summary>
    public static string GetArtifactRelativePath(string fileSha256, string fileUrl)
    {
        if (!TryGetFileName(fileUrl, out var fileName))
        {
            throw new ArgumentException("The artifact URL does not end in a usable file name.", nameof(fileUrl));
        }

        return ArtifactDirectoryName + "/" + fileSha256 + "/" + fileName;
    }

    /// <summary>
    /// A download's temporary path beside its destination: <c>&lt;file name&gt;.&lt;32 lowercase hex&gt;.partial</c>.
    /// </summary>
    public static string CreateTemporaryDownloadPath(string destinationPath) =>
        destinationPath + "." + Guid.NewGuid().ToString("N") + TemporaryDownloadExtension;

    /// <summary>
    /// Whether a file name is exactly one <see cref="CreateTemporaryDownloadPath" /> produces, so that
    /// nothing else in an artifact directory is ever mistaken for an abandoned download.
    /// </summary>
    public static bool IsTemporaryDownloadName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (!fileName.EndsWith(TemporaryDownloadExtension, StringComparison.Ordinal))
        {
            return false;
        }

        var stem = fileName[..^TemporaryDownloadExtension.Length];
        var separator = stem.LastIndexOf('.');
        return separator > 0 &&
            stem.Length - separator - 1 == TemporaryNameTokenLength &&
            IsLowercaseHex(stem.AsSpan(separator + 1));
    }

    public static bool TryGetFileName(string? fileUrl, out string fileName)
    {
        fileName = string.Empty;
        if (!Uri.TryCreate(fileUrl, UriKind.Absolute, out var uri) || uri.Segments.Length == 0)
        {
            return false;
        }

        var candidate = Uri.UnescapeDataString(uri.Segments[^1]);
        if (candidate.Length == 0 ||
            candidate is "." or ".." ||
            candidate.IndexOfAny(['/', '\\', ':']) >= 0 ||
            candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        fileName = candidate;
        return true;
    }

    /// <summary>
    /// Resolves a manifest's relative artifact path against the model directory, refusing any path
    /// that is rooted or climbs out of the directory. The manifest sits on a writable volume, so what
    /// it names is checked rather than trusted.
    /// </summary>
    public static bool TryResolve(string modelDirectory, string? relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        var root = Path.GetFullPath(modelDirectory);
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(rootWithSeparator, comparison))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    public static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && IsLowercaseHex(value);

    public static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static bool IsLowercaseHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
