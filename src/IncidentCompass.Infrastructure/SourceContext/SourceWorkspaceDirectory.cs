namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// Creation and removal of the one directory a materialization owns.
/// </summary>
/// <remarks>
/// Every workspace directory is a fresh child of the configured workspace root, named with
/// <see cref="NamePrefix"/> and a random suffix, so two concurrent materializations never share a
/// directory and a leftover is recognizable by name. That prefix is the contract a reaper will
/// match on: the bound on accumulated workspaces does not live here, because deletion on every
/// terminal path cannot cover the process being killed between the copy and the delete. The reaper
/// belongs beside the existing retention operations, as a third bounded run in the Worker's
/// retention pass, deleting prefixed directories under the workspace root older than a configured
/// window. Until it exists, a crash leaks one directory, and the operator-visible consequence is
/// disk under the workspace root, which is why that root is configured rather than implicit.
/// </remarks>
internal static class SourceWorkspaceDirectory
{
    public const string NamePrefix = "source-workspace-";

    public static string Create(string workspaceRoot)
    {
        var path = Path.Combine(workspaceRoot, NamePrefix + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Removes a workspace, tolerating a directory that is already gone or that the process cannot
    /// delete. Deletion runs on failure paths and from <see cref="IDisposable.Dispose"/>, where
    /// throwing would replace the real error with a cleanup error; what this cannot remove is
    /// exactly what the reaper described above is for.
    /// </summary>
    public static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
