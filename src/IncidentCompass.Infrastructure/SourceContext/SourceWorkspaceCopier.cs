using System.Security.Cryptography;
using static IncidentCompass.Infrastructure.SourceContext.SourcePathBoundary;

namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// The bounded walk that copies the monitored tree into a workspace directory and accumulates the
/// entries a tree identity is computed from. One instance copies one tree once.
/// </summary>
/// <remarks>
/// The walk only ever reads below the canonical monitored root and only ever writes below the
/// workspace directory it is handed. Containment is rechecked per entry against the same
/// <see cref="SourcePathBoundary"/> the source-lookup path uses, rather than a second boundary
/// written for this walk. Every refusal stops the walk immediately and returns a code; the caller
/// deletes the partial copy, so no bound is ever satisfied by truncation.
/// </remarks>
internal sealed class SourceWorkspaceCopier(string canonicalRoot, SourceWorkspaceBounds bounds)
{
    private const int CopyBufferBytes = 64 * 1024;

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly List<SourceTreeEntry> entries = [];
    private readonly byte[] buffer = new byte[CopyBufferBytes];
    private long totalBytes;

    public IReadOnlyList<SourceTreeEntry> Entries => entries;

    public long TotalBytes => totalBytes;

    /// <summary>Copies the tree, returning a refusal code or <c>null</c> when the whole tree was copied.</summary>
    public Task<string?> CopyAsync(string workspacePath, CancellationToken cancellationToken) =>
        CopyDirectoryAsync(canonicalRoot, workspacePath, string.Empty, 1, cancellationToken);

    private async Task<string?> CopyDirectoryAsync(
        string sourceDirectory,
        string targetDirectory,
        string relativePrefix,
        int depth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > bounds.MaxDepth)
        {
            return SourceWorkspaceCodes.DepthLimit;
        }

        foreach (var entry in new DirectoryInfo(sourceDirectory).EnumerateFileSystemInfos())
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

            if (!IsUnderRoot(entry.FullName, canonicalRoot, PathComparison))
            {
                return SourceWorkspaceCodes.PathRejected;
            }

            var relativePath = relativePrefix.Length == 0
                ? entry.Name
                : relativePrefix + "/" + entry.Name;
            var target = Path.Combine(targetDirectory, entry.Name);
            var nested = entry is DirectoryInfo directory
                ? await CopyChildDirectoryAsync(directory, target, relativePath, depth, cancellationToken)
                : await CopyFileAsync((FileInfo)entry, target, relativePath, cancellationToken);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private async Task<string?> CopyChildDirectoryAsync(
        DirectoryInfo directory,
        string target,
        string relativePath,
        int depth,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(target);
        return await CopyDirectoryAsync(directory.FullName, target, relativePath, depth + 1, cancellationToken);
    }

    private async Task<string?> CopyFileAsync(
        FileInfo file,
        string target,
        string relativePath,
        CancellationToken cancellationToken)
    {
        if (entries.Count >= bounds.MaxFiles)
        {
            return SourceWorkspaceCodes.FileLimit;
        }

        var remaining = bounds.MaxTotalBytes - totalBytes;
        if (file.Length > remaining)
        {
            return SourceWorkspaceCodes.SizeLimit;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var copied = 0L;
        await using (var source = Open(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var destination = Open(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                // The reported length is a pre-read observation, so a file that grows underneath the
                // copy is bounded by what was actually read rather than by what was promised.
                copied += read;
                if (copied > remaining)
                {
                    return SourceWorkspaceCodes.SizeLimit;
                }

                hash.AppendData(buffer.AsSpan(0, read));
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }

        totalBytes += copied;
        entries.Add(new SourceTreeEntry(relativePath, Convert.ToHexStringLower(hash.GetHashAndReset())));
        return null;
    }

    private static FileStream Open(string path, FileMode mode, FileAccess access, FileShare share) =>
        new(path, mode, access, share, CopyBufferBytes, useAsync: true);
}
