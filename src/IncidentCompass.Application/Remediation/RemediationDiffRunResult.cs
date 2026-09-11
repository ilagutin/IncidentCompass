namespace IncidentCompass.Application.Remediation;

/// <summary>
/// The outcome of one remediation pass: either a durable diff record, or a refusal code and nothing
/// else.
/// </summary>
/// <remarks>
/// There is no third state. A pass that produced a diff it could not persist reports a refusal, not
/// a success with a record somewhere: a caller told the pass succeeded would go on to propose an
/// action citing a record that is not there.
/// </remarks>
internal sealed record RemediationDiffRunResult(string Code, RemediationDiff? Diff)
{
    public static RemediationDiffRunResult Produced(RemediationDiff diff) =>
        new(RemediationCodes.Produced, diff);

    public static RemediationDiffRunResult Refused(string code) => new(code, null);

    public bool IsProduced => Diff is not null;
}
