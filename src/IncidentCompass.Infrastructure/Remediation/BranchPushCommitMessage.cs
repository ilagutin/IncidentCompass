using System.Globalization;
using System.Text;
using IncidentCompass.Application.Remediation;

namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// The commit message a governed push writes: backend-composed, and carrying no model text.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why none of the message comes from the change.</b> A commit message is the first thing a human
/// reads about a branch and the last place attacker-influenced text should appear. The diff is model
/// output derived from incident data a stranger can influence, and a summary of it in the subject line
/// would put that text in a reviewer's terminal, in a notification, and in any tool that renders git
/// history. So the message states only backend facts: which report this answers, what was proved, what
/// was excluded, and that nothing checked the change.
/// </para>
/// <para>
/// <b>It is deterministic.</b> Every value in it is frozen in the approval, so the same approval
/// produces the same message, which is one of the things that makes the commit content-addressable to
/// the same name on every attempt.
/// </para>
/// </remarks>
internal static class BranchPushCommitMessage
{
    public static string For(BranchPushPayload payload)
    {
        var builder = new StringBuilder("Governed remediation for incident report ")
            .Append(payload.OriginReportId.ToString("N"))
            .Append("\n\n")
            .Append("Service: ").Append(payload.ServiceName).Append('\n')
            .Append("Release: ").Append(payload.Release).Append('\n')
            .Append("Base tree identity: ").Append(payload.BaseTreeIdentity).Append('\n')
            .Append("Result tree identity: ").Append(payload.ResultTreeIdentity).Append('\n')
            .Append("Correspondence digest: ").Append(payload.CorrespondenceDigest).Append('\n')
            .Append("Proved paths: ").Append(Count(payload.ProvedPathCount)).Append('\n')
            .Append("Excluded local-only paths: ").Append(Count(payload.ExcludedPathCount)).Append('\n')
            .Append("Approved code-write action: ").Append(payload.PredecessorActionId.ToString("N"))
            .Append("\n\n")
            .Append("No test was executed. This product starts no process and runs no test command, ")
            .Append("so nothing has checked that this change builds or behaves.\n");
        return builder.ToString();
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
