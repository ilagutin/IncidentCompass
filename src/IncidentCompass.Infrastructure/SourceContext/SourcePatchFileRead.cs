namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// One step of parsing: a file section and where reading should continue, or a refusal code.
/// </summary>
internal sealed record SourcePatchFileRead(SourcePatchFile? File, string? Code, int NextIndex)
{
    public static SourcePatchFileRead Read(SourcePatchFile file, int nextIndex) => new(file, null, nextIndex);

    public static SourcePatchFileRead Refused(string code) => new(null, code, 0);
}
