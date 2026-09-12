namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// One file section of a patch: the repository-relative path it names, what it does to that file,
/// and its hunks in base order.
/// </summary>
/// <param name="RepositoryPath">
/// Always <c>/</c>-separated, always relative, already checked against the path policy. The old and
/// new sides of the section named the same path, since a rename is refused rather than parsed.
/// </param>
/// <param name="Kind">What the section does to that file.</param>
/// <param name="Hunks">The hunks, in ascending base order and known not to overlap.</param>
internal sealed record SourcePatchFile(
    string RepositoryPath,
    SourcePatchFileKind Kind,
    IReadOnlyList<SourcePatchHunk> Hunks);
