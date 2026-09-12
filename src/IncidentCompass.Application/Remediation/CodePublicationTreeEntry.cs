namespace IncidentCompass.Application.Remediation;

/// <summary>
/// One overlay a push lays over the base commit's tree.
/// </summary>
/// <param name="RepositoryPath">The path the approved diff names, already policy-checked.</param>
/// <param name="FileMode">
/// The git file mode the entry keeps: the mode the base tree already gave this path, or the ordinary
/// non-executable mode for a path the diff creates. It is never widened.
/// </param>
/// <param name="Content">
/// Exact post-patch bytes, or <see langword="null" /> to remove the path from the pushed tree.
/// </param>
public sealed record CodePublicationTreeEntry(
    string RepositoryPath,
    string FileMode,
    byte[]? Content);
