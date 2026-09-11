namespace IncidentCompass.Application.Remediation;

/// <summary>
/// The remote base a push would build on: one commit, its tree, and what that tree holds.
/// </summary>
/// <param name="Code">The outcome, from <see cref="CodePublicationCodes" />.</param>
/// <param name="CommitSha">The commit the configured base branch points at. Null on a refusal.</param>
/// <param name="TreeSha">That commit's tree. Null on a refusal.</param>
/// <param name="BlobsByPath">
/// Repository path to git blob id, for every regular file the tree holds. The adapter refuses the
/// whole read rather than returning a partial listing, so this is the entire remote side or nothing.
/// </param>
/// <param name="FileModesByPath">
/// Repository path to git file mode for the same entries. A push that rewrites a file keeps the mode
/// the base already gave it; without this an executable file would come back non-executable, which
/// is a change nobody approved.
/// </param>
public sealed record CodePublicationBaseResult(
    string Code,
    string? CommitSha,
    string? TreeSha,
    IReadOnlyDictionary<string, string> BlobsByPath,
    IReadOnlyDictionary<string, string> FileModesByPath)
{
    public static CodePublicationBaseResult Refused(string code) =>
        new(code, null, null, new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));

    public bool IsRead => CommitSha is not null && TreeSha is not null;
}
