namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// Walks a materialized workspace and names every admitted file the way git names one, producing the
/// local half of a correspondence proof.
/// </summary>
/// <remarks>
/// <para>
/// It is a second walk rather than a second hash inside the copier on purpose. The copier runs on
/// every source read and every remediation pass; a git blob id is needed only when a branch is about
/// to be pushed, which is off in the shipped configuration. Paying for it in the copier would charge
/// every read for a capability almost no read uses.
/// </para>
/// <para>
/// It admits exactly what the copier admitted, because it walks the copy rather than the monitored
/// root: the copy already holds only admitted files, and the same
/// <see cref="SourceWorkspaceEntryPolicy" /> is consulted anyway so that a refusal inside the
/// workspace is impossible to miss. Paths are built the same way, <c>/</c>-separated and relative,
/// which is the condition for comparing them with a remote listing at all.
/// </para>
/// </remarks>
internal sealed class GitBlobTreeScanner(SourceWorkspaceBounds bounds)
{
    private const int BufferBytes = 64 * 1024;

    private readonly Dictionary<string, string> entries = new(StringComparer.Ordinal);
    private readonly byte[] buffer = new byte[BufferBytes];

    /// <summary>
    /// Returns repository path to lower-hex git blob id for the whole workspace, or a refusal code.
    /// </summary>
    public static async Task<(IReadOnlyDictionary<string, string>? Entries, string? Code)> ScanAsync(
        string workspacePath,
        SourceWorkspaceBounds bounds,
        CancellationToken cancellationToken)
    {
        var scanner = new GitBlobTreeScanner(bounds);
        var refusal = await scanner.ScanDirectoryAsync(workspacePath, string.Empty, 1, cancellationToken);
        return refusal is null ? (scanner.entries, null) : (null, refusal);
    }

    private async Task<string?> ScanDirectoryAsync(
        string directory,
        string relativePrefix,
        int depth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > bounds.MaxDepth)
        {
            return SourceWorkspaceCodes.DepthLimit;
        }

        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var refusal = SourceWorkspaceEntryPolicy.Reject(entry.Attributes, entry.Name, depth);
            if (refusal is not null)
            {
                return refusal;
            }

            if (SourceWorkspaceEntryPolicy.IsExcluded(entry.Name, depth))
            {
                continue;
            }

            var relativePath = relativePrefix.Length == 0 ? entry.Name : relativePrefix + "/" + entry.Name;
            var nested = entry is DirectoryInfo child
                ? await ScanDirectoryAsync(child.FullName, relativePath, depth + 1, cancellationToken)
                : await NameFileAsync((FileInfo)entry, relativePath, cancellationToken);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private async Task<string?> NameFileAsync(
        FileInfo file,
        string relativePath,
        CancellationToken cancellationToken)
    {
        if (entries.Count >= bounds.MaxFiles)
        {
            return SourceWorkspaceCodes.FileLimit;
        }

        await using var stream = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        entries[relativePath] = await GitBlobIdentity.ComputeAsync(
            stream, stream.Length, buffer, cancellationToken);
        return null;
    }
}
