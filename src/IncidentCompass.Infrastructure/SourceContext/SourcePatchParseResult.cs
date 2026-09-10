namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// The outcome of parsing: either a patch, or a refusal code and nothing else.
/// </summary>
internal sealed record SourcePatchParseResult(SourcePatch? Patch, string Code)
{
    public static SourcePatchParseResult Parsed(SourcePatch patch) => new(patch, SourcePatchCodes.Parsed);

    public static SourcePatchParseResult Refused(string code) => new(null, code);

    public bool IsParsed => Patch is not null;
}
