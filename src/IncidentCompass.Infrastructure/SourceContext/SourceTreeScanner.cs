using System.Security.Cryptography;

namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// Walks a tree that already exists and produces the entries its identity is computed from, without
/// copying anything. One instance scans one tree once.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes an identity recomputable. The copier produces entries as a by-product of
/// copying, which can only ever describe the tree at the instant it was copied; recomputing after a
/// change needs a walk that reads a tree it did not create. The two admit exactly the same entries,
/// because both consult <see cref="SourceWorkspaceEntryPolicy"/> and build the same
/// <c>/</c>-separated relative paths, which is the condition for their identities to be comparable
/// at all. A scan of a freshly materialized workspace therefore reproduces the identity the
/// materialization recorded, and a test asserts exactly that rather than leaving it to inspection.
/// </para>
/// <para>
/// The same bounds apply, and for the same reason: a walk that stopped early would produce an
/// identity for a tree that exists nowhere.
/// </para>
/// </remarks>
internal sealed class SourceTreeScanner(SourceWorkspaceBounds bounds)
{
    private readonly List<SourceTreeEntry> entries = [];
    private long totalBytes;

    public static async Task<SourceTreeScanResult> ScanAsync(
        string root,
        SourceWorkspaceBounds bounds,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            return SourceTreeScanResult.Refused(SourceWorkspaceCodes.RootUnavailable);
        }

        var scanner = new SourceTreeScanner(bounds);
        var refusal = await scanner.ScanDirectoryAsync(
            Path.GetFullPath(root),
            string.Empty,
            1,
            cancellationToken);
        return refusal is null
            ? SourceTreeScanResult.Scanned(scanner.entries)
            : SourceTreeScanResult.Refused(refusal);
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
                : await HashFileAsync((FileInfo)entry, relativePath, cancellationToken);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private async Task<string?> HashFileAsync(FileInfo file, string relativePath, CancellationToken cancellationToken)
    {
        if (entries.Count >= bounds.MaxFiles)
        {
            return SourceWorkspaceCodes.FileLimit;
        }

        if (file.Length > bounds.MaxTotalBytes - totalBytes)
        {
            return SourceWorkspaceCodes.SizeLimit;
        }

        await using var stream = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        totalBytes += file.Length;
        entries.Add(new SourceTreeEntry(relativePath, Convert.ToHexStringLower(hash)));
        return null;
    }
}
