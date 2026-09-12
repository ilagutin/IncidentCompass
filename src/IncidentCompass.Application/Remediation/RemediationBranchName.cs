namespace IncidentCompass.Application.Remediation;

/// <summary>
/// The one branch name a remediation push may create, derived from the report it answers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is derived and not chosen.</b> A name a model wrote would be the one piece of attacker
/// influenced text in a request that creates a reference, and the ways a branch name can be abused -
/// a name that collides with a protected branch, a name carrying a path segment, a name that reads as
/// a different feature's branch - are exactly the ways a free-text field goes wrong. Deriving it from
/// the origin report id removes the field: there is nothing to sanitize because there is nothing to
/// supply.
/// </para>
/// <para>
/// <b>What the derivation buys beyond safety.</b> The name is unique to one report and stable across
/// every attempt, so it is also the reconciliation key: an uncertain outcome is settled by reading
/// this one reference, and a replay finds the branch it would have created already pointing at the
/// commit it would have created. The prefix keeps every branch this product creates in one namespace
/// an operator can protect, mirror or delete as a group.
/// </para>
/// <para>
/// <b>It is inside the approval hash.</b> The frozen payload carries the name, so a change to this
/// derivation invalidates every standing approval rather than quietly retargeting one.
/// </para>
/// </remarks>
public static class RemediationBranchName
{
    /// <summary>The namespace every branch this product creates lives in.</summary>
    public const string Prefix = "incidentcompass/remediation/";

    /// <summary>Characters in a derived name: the prefix plus a 32-character report id.</summary>
    public const int Characters = 60;

    public static string For(Guid originReportId) => Prefix + originReportId.ToString("N");

    /// <summary>
    /// Whether a value is exactly a name this type could have produced. Used where a name arrives
    /// from durable state rather than from a call, so that a row edited by hand cannot widen what a
    /// push may create.
    /// </summary>
    public static bool IsDerived(string? value, Guid originReportId) =>
        value is { Length: Characters } &&
        string.Equals(value, For(originReportId), StringComparison.Ordinal);
}
