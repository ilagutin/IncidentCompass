using System.Globalization;

namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// Creation and removal of the one directory a materialization owns, and the reading back of the
/// instant a directory says it was created at.
/// </summary>
/// <remarks>
/// <para>
/// Every workspace directory is a fresh child of the configured workspace root, named with
/// <see cref="NamePrefix"/>, the UTC instant it was created at, and a random suffix, so two
/// concurrent materializations never share a directory and a leftover is recognizable by name. That
/// prefix is the contract <see cref="AbandonedSourceWorkspaceReaper"/> matches on, and the instant
/// in the name is what that reaper ages a leftover by. The bound on accumulated workspaces does not
/// live here, because deletion on every terminal path cannot cover the process being killed between
/// the copy and the delete; it lives in the Worker's retention pass, as a third bounded run beside
/// signal compaction and stale-artifact reaping.
/// </para>
/// <para>
/// The instant is carried in the name rather than left to the filesystem because a directory's
/// creation time is not portable: on Linux it is the filesystem's birth time when the filesystem has
/// one and an unknown sentinel when it does not, and it cannot be set back for a test. A name is
/// written once by the process that created the directory, never drifts, and reads the same on every
/// platform. Its resolution is one second, which is four orders of magnitude finer than the smallest
/// window <see cref="SourceWorkspaceRetentionOptionsValidator"/> admits.
/// </para>
/// </remarks>
internal static class SourceWorkspaceDirectory
{
    public const string NamePrefix = "source-workspace-";

    /// <summary>
    /// Fixed-width UTC instant, so the timestamp always occupies the same span of the name and a
    /// listing sorts oldest first for an operator reading the workspace root by eye.
    /// </summary>
    private const string TimestampFormat = "yyyyMMdd'T'HHmmss'Z'";

    private const int TimestampLength = 16;

    public static string Create(string workspaceRoot) =>
        Create(workspaceRoot, DateTimeOffset.UtcNow);

    public static string Create(string workspaceRoot, DateTimeOffset createdAtUtc)
    {
        var name = NamePrefix +
            createdAtUtc.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture) +
            "-" + Guid.NewGuid().ToString("n");
        var path = Path.Combine(workspaceRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Reads the creation instant a workspace directory name states, or returns <see langword="false"/>
    /// for any name this type did not write.
    /// </summary>
    /// <remarks>
    /// Fails closed on purpose. A directory under the workspace root whose name does not carry a
    /// readable instant is something this code cannot date, and the reaper must leave what it cannot
    /// date alone rather than guess an age for it.
    /// </remarks>
    public static bool TryReadCreatedAtUtc(string directoryName, out DateTimeOffset createdAtUtc)
    {
        createdAtUtc = default;
        if (!directoryName.StartsWith(NamePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = directoryName.AsSpan(NamePrefix.Length);
        if (remainder.Length < TimestampLength + 2 || remainder[TimestampLength] != '-')
        {
            return false;
        }

        return DateTimeOffset.TryParseExact(
            remainder[..TimestampLength],
            TimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out createdAtUtc);
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
