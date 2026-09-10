namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// A materialized copy of the monitored source tree, and the identity of what was copied. Disposing
/// it removes the directory; it owns nothing else, holds no handle open and starts no process.
/// </summary>
internal sealed class SourceWorkspace(
    string directoryPath,
    string treeIdentity,
    int fileCount,
    long totalBytes) : IDisposable
{
    /// <summary>Absolute path of the workspace directory. A host path, so it is never logged.</summary>
    public string DirectoryPath { get; } = directoryPath;

    /// <summary>Lower-hex SHA-256 tree identity of the copied files. See <see cref="SourceTreeIdentity"/>.</summary>
    public string TreeIdentity { get; } = treeIdentity;

    public int FileCount { get; } = fileCount;

    public long TotalBytes { get; } = totalBytes;

    public void Dispose()
    {
        SourceWorkspaceDirectory.TryDelete(DirectoryPath);
        GC.SuppressFinalize(this);
    }
}
