namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// The whole patch decided against the base: every file's original and result bytes, or the refusal
/// that stopped the decision. A plan exists only when every file passed, so there is no such thing
/// as a plan for part of a patch.
/// </summary>
internal sealed record SourcePatchPlan(IReadOnlyList<SourcePatchPlannedFile>? Files, string Code)
{
    public static SourcePatchPlan Planned(IReadOnlyList<SourcePatchPlannedFile> files) =>
        new(files, SourcePatchCodes.Parsed);

    public static SourcePatchPlan Refused(string code) => new(null, code);
}
