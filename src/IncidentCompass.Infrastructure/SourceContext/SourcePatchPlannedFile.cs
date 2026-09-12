namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// One file's whole change, decided before anything is written: where it is, what it held, and what
/// it will hold.
/// </summary>
/// <param name="RepositoryPath">The <c>/</c>-separated path the patch named.</param>
/// <param name="AbsolutePath">
/// Canonicalized and rechecked to be below the workspace. A host path, so it is never logged.
/// </param>
/// <param name="Kind">What the change does to the file.</param>
/// <param name="OriginalContent">
/// The exact bytes the file held, kept for the whole attempt because that is what a failed write is
/// rolled back to. It is <c>null</c> only for a create, where the file had no bytes.
/// </param>
/// <param name="ResultContent">
/// The exact bytes to write. It is <c>null</c> only for a delete, where the file is to have none.
/// </param>
internal sealed record SourcePatchPlannedFile(
    string RepositoryPath,
    string AbsolutePath,
    SourcePatchFileKind Kind,
    byte[]? OriginalContent,
    byte[]? ResultContent);
