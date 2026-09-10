namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// A parsed, structurally validated patch. Holding one means the text parsed as the accepted subset
/// and passed every check that does not need a filesystem; it does not mean the patch applies, which
/// only <see cref="SourcePatchApplier"/> can say.
/// </summary>
internal sealed record SourcePatch(IReadOnlyList<SourcePatchFile> Files);
