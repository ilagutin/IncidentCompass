namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <summary>
/// Where the local embedding model's files live inside the model directory.
/// <para>
/// The directory holds one active manifest at its root and each artifact file under
/// <c>artifacts/&lt;that file's SHA-256&gt;/&lt;file name&gt;</c>. Every file is addressed by its own
/// digest, so a file of any other model or tokenizer never shares a path with an installed one. A
/// later install can place new files beside the old ones, including an install that keeps the model
/// and changes only the tokenizer, and switch by replacing the manifest alone; a rollback is
/// restoring the old manifest.
/// </para>
/// </summary>
internal static class LocalOnnxModelLayout
{
    public const string ManifestFileName = "manifest.json";

    public const string ArtifactDirectoryName = "artifacts";

    public static string GetManifestPath(string modelDirectory) =>
        Path.Combine(modelDirectory, ManifestFileName);

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
        value is { Length: 64 } && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}
