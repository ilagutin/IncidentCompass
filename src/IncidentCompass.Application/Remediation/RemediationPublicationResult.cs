namespace IncidentCompass.Application.Remediation;

/// <summary>
/// What one publication preparation concluded: either the files a push may carry plus the proof that
/// the approved base is the remote commit, or a code and nothing else.
/// </summary>
/// <param name="Code">
/// The outcome, from the closed vocabularies described on <see cref="RemediationPublicationCodes" />.
/// A refusal is a code and nothing else: no path, no line and no byte of a file.
/// </param>
/// <param name="CorrespondenceDigest">
/// The frozen statement of what was proved. <see langword="null" /> on every refusal.
/// </param>
/// <param name="ProvedPathCount">How many paths were proved byte-identical. Zero on every refusal.</param>
/// <param name="LocalOnlyPaths">
/// Paths the approved base holds that the remote tree does not, in ordinal order. They are excluded
/// from the push, and they are listed rather than counted so that a reviewer and a test can both see
/// which ones. Empty on every refusal.
/// </param>
/// <param name="ChangedFiles">
/// The files the approved diff writes, with their exact post-patch bytes. Empty on every refusal.
/// </param>
public sealed record RemediationPublicationResult(
    string Code,
    string? CorrespondenceDigest,
    int ProvedPathCount,
    IReadOnlyList<string> LocalOnlyPaths,
    IReadOnlyList<RemediationPublicationFile> ChangedFiles)
{
    public static RemediationPublicationResult Prepared(
        string correspondenceDigest,
        int provedPathCount,
        IReadOnlyList<string> localOnlyPaths,
        IReadOnlyList<RemediationPublicationFile> changedFiles) =>
        new(
            RemediationPublicationCodes.Prepared,
            correspondenceDigest,
            provedPathCount,
            localOnlyPaths,
            changedFiles);

    public static RemediationPublicationResult Refused(string code) => new(code, null, 0, [], []);

    public bool IsPrepared => CorrespondenceDigest is not null;
}
