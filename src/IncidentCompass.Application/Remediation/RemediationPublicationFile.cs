namespace IncidentCompass.Application.Remediation;

/// <summary>
/// One file the approved diff writes, read back out of the patched copy as exact bytes.
/// </summary>
/// <param name="RepositoryPath">
/// Repository-relative, <c>/</c>-separated, and already checked against the patch path policy: no
/// absolute path, no traversal segment, no <c>.git</c> segment, no secret name, and an admitted
/// extension. It is the path the diff named, so it is model text, and it passed that policy before a
/// byte of it was written anywhere.
/// </param>
/// <param name="Content">
/// The file's bytes after the diff applied, or <see langword="null" /> when the diff removes the
/// path. A removal is a real outcome of the supported patch subset and has to be expressible, or a
/// pushed tree would keep a file the approved change deleted.
/// </param>
public sealed record RemediationPublicationFile(string RepositoryPath, byte[]? Content);
