namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// What one materialization may consume. Every bound is a refusal, never a truncation: a workspace
/// that stopped early would carry a tree identity for a tree that does not exist anywhere, and a
/// later reader could not tell the difference. Exceeding any bound therefore deletes the partial
/// copy and returns a code.
/// </summary>
/// <param name="MaxFiles">Files admitted into the copy. The count excludes skipped entries.</param>
/// <param name="MaxTotalBytes">Bytes admitted into the copy, summed across all files.</param>
/// <param name="MaxDepth">
/// Path segments below the monitored root. Root children are depth 1. The bound keeps a pathological
/// tree from exhausting the stack, and keeps a deep copy inside the path-length limits of the
/// tightest filesystem this runs on.
/// </param>
internal sealed record SourceWorkspaceBounds(int MaxFiles, long MaxTotalBytes, int MaxDepth)
{
    /// <summary>
    /// Sized for a service checkout rather than a monorepo: roughly a large repository's file count,
    /// a copy that fits a container's writable layer, and a depth no ordinary source tree reaches.
    /// </summary>
    public static SourceWorkspaceBounds Default { get; } = new(20_000, 128L * 1024 * 1024, 32);
}
