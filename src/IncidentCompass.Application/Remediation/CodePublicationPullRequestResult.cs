namespace IncidentCompass.Application.Remediation;

/// <summary>
/// What one pull request is, or became: a code and, when a pull request answers for the head that was
/// asked about, its number.
/// </summary>
/// <param name="Code">The outcome, from <see cref="CodePublicationCodes" />.</param>
/// <param name="Number">
/// The provider's pull-request number, or <see langword="null" /> when none exists, the answer did not
/// match what was asked about, or the outcome is not known. It is the one identifier that leaves this
/// port: no URL, no title, no author and no branch reference the provider echoed.
/// </param>
public sealed record CodePublicationPullRequestResult(string Code, int? Number)
{
    public static CodePublicationPullRequestResult Refused(string code) => new(code, null);
}
