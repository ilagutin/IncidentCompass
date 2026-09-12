namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// One step of parsing: a hunk and where reading should continue, or a refusal code.
/// </summary>
internal sealed record SourcePatchHunkRead(SourcePatchHunk? Hunk, string? Code, int NextIndex)
{
    public static SourcePatchHunkRead Read(SourcePatchHunk hunk, int nextIndex) => new(hunk, null, nextIndex);

    public static SourcePatchHunkRead Refused(string code) => new(null, code, 0);
}
