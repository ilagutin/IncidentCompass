namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// What comparing an approved local base against one remote commit's tree concluded.
/// </summary>
/// <param name="Code">The outcome, from <see cref="GitTreeCorrespondenceCodes" />.</param>
/// <param name="ProvedPathCount">
/// How many paths were proved byte-identical on both sides. Zero on every refusal.
/// </param>
/// <param name="LocalOnlyPaths">
/// Repository-relative paths the local base holds and the remote tree does not, in ordinal order.
/// These are enumerated so that excluding them is a stated decision rather than a silent one; they
/// are never added to the pushed tree. Empty on every refusal.
/// </param>
/// <param name="Digest">
/// A lower-hex SHA-256 over exactly what was proved: the base identity, the remote commit and tree,
/// the proved count, and every excluded path. Freezing it into an approval is what makes the claim
/// re-checkable later rather than a sentence in a review summary. <see langword="null" /> on every
/// refusal.
/// </param>
internal sealed record GitTreeCorrespondenceOutcome(
    string Code,
    int ProvedPathCount,
    IReadOnlyList<string> LocalOnlyPaths,
    string? Digest)
{
    public static GitTreeCorrespondenceOutcome Refused(string code) => new(code, 0, [], null);

    public bool IsProved => Digest is not null;
}
