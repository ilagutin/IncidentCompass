namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <summary>
/// Removes abandoned downloads: temporary files a killed install left in an artifact directory.
/// <para>
/// A file is removed only when its name is exactly the store's temporary download name, it sits
/// directly in a digest-named artifact directory, and it was last written longer ago than the install
/// timeout. The age rule is what makes this safe beside a concurrent installer on the same volume.
/// Both install paths, the Worker's start-time install pass and the <c>memory model install</c>
/// command, run under <see cref="LocalOnnxEmbeddingOptions.InstallTimeoutSeconds" /> and are cancelled
/// once it has passed; a live install creates its temporary file after it starts, so a temporary file
/// untouched for longer than that timeout cannot belong to an install that is still running. This
/// holds while the installers share the configured timeout, which they do when they read the same
/// host configuration. Removal is best effort; a file that cannot be removed is left for the next
/// pass.
/// </para>
/// </summary>
internal static class LocalOnnxModelTemporaryFileSweeper
{
    public static int RemoveStaleDownloads(string modelDirectory, TimeSpan staleAfter, DateTimeOffset now)
    {
        var artifactsDirectory = Path.Combine(modelDirectory, LocalOnnxModelLayout.ArtifactDirectoryName);
        if (!Directory.Exists(artifactsDirectory))
        {
            return 0;
        }

        var staleBefore = (now - staleAfter).UtcDateTime;
        var removed = 0;
        foreach (var digestDirectory in Directory.EnumerateDirectories(artifactsDirectory))
        {
            if (!LocalOnnxModelLayout.IsSha256Hex(Path.GetFileName(digestDirectory)))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(
                         digestDirectory,
                         "*" + LocalOnnxModelLayout.TemporaryDownloadExtension,
                         SearchOption.TopDirectoryOnly))
            {
                if (LocalOnnxModelLayout.IsTemporaryDownloadName(Path.GetFileName(path)) &&
                    File.GetLastWriteTimeUtc(path) < staleBefore &&
                    TryRemove(path))
                {
                    removed++;
                }
            }
        }

        return removed;
    }

    private static bool TryRemove(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
