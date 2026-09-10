namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// The outcome of one attempt to apply a patch: either the whole patch was applied and the tree has
/// a new identity, or nothing was applied and the workspace holds what it held.
/// </summary>
/// <param name="Code">The outcome, from <see cref="SourcePatchCodes"/>.</param>
/// <param name="TreeIdentity">
/// Recomputed by walking the workspace after the change, not derived from what the applier believes
/// it wrote. An artifact can therefore carry the base it applied to and the result it produced, and
/// both are statements about bytes on disk.
/// </param>
/// <param name="FilesChanged">How many file sections were written. Zero on every refusal.</param>
internal sealed record SourcePatchApplyResult(string Code, string? TreeIdentity, int FilesChanged)
{
    public static SourcePatchApplyResult Applied(string treeIdentity, int filesChanged) =>
        new(SourcePatchCodes.Applied, treeIdentity, filesChanged);

    public static SourcePatchApplyResult Refused(string code) => new(code, null, 0);

    public bool IsApplied => TreeIdentity is not null;
}
