namespace IncidentCompass.Application.Remediation;

/// <summary>
/// What one branch reference is, or became: a code and, when the reference exists, the commit it
/// points at.
/// </summary>
/// <param name="Code">The outcome, from <see cref="CodePublicationCodes" />.</param>
/// <param name="CommitSha">
/// The commit the reference points at, or <see langword="null" /> when it does not exist or the
/// outcome is not known.
/// </param>
public sealed record CodePublicationRefResult(string Code, string? CommitSha)
{
    public static CodePublicationRefResult Refused(string code) => new(code, null);
}
