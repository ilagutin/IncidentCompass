namespace IncidentCompass.Application.Remediation;

/// <summary>
/// The outcome of naming a base: either the tree identity a change would be prepared against, or a
/// refusal code and nothing else.
/// </summary>
/// <param name="Code">
/// The outcome, from the closed vocabulary described on <see cref="RemediationCodes" />.
/// </param>
/// <param name="TreeIdentity">
/// The lower-hex SHA-256 content identity of the tree, or <see langword="null" /> on a refusal. It
/// is a statement about bytes, not a commit id: nothing behind the port reads git.
/// </param>
public sealed record RemediationBaseResult(string Code, string? TreeIdentity)
{
    public static RemediationBaseResult Identified(string treeIdentity) =>
        new(RemediationCodes.BaseIdentified, treeIdentity);

    public static RemediationBaseResult Refused(string code) => new(code, null);

    public bool IsIdentified => TreeIdentity is not null;
}
