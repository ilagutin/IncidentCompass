namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// One file's contribution to a tree identity: its path relative to the monitored root, always with
/// <c>/</c> separators and with the case the filesystem reported, and the lower-hex SHA-256 of its
/// exact bytes.
/// </summary>
internal sealed record SourceTreeEntry(string RepositoryPath, string ContentSha256);
