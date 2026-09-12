using System.Globalization;
using System.Text;

namespace IncidentCompass.Application.Remediation;

/// <summary>
/// The title and description a governed pull request carries. Backend-composed, bounded, and made only
/// of values whose shape is fixed.
/// </summary>
/// <remarks>
/// <para>
/// <b>A pull-request description is a public page, so it has no free-text field at all.</b> Everything
/// the body renders is one of four things: a report identifier, an issue number, one of three
/// confidence words, or a count or hex digest. The service name and the release are deliberately left
/// out even though the approval carries them, because they are the only values on this path that an
/// ingested signal can influence, and a public page is the wrong place to find out how a provider
/// renders a name someone chose. They stay in the reviewer's summary, which no stranger reads.
/// </para>
/// <para>
/// <b>What that rules out by construction.</b> No credential, because none exists above the gateway.
/// No absolute host path, because no path of any kind is rendered. No source body and no diff, because
/// the patch text is not a parameter here. No prompt and no model output, because nothing a model wrote
/// reaches this type. There is nothing to escape, because there is nothing to escape.
/// </para>
/// <para>
/// <b>It is deterministic.</b> The same payload composes the same bytes, which is what lets a dispatch
/// re-derive the text and refuse anything that differs from what a person approved.
/// </para>
/// </remarks>
internal static class PullRequestNarrative
{
    /// <summary>Maximum UTF-8 bytes in a title, the bound the issue adapter already uses.</summary>
    public const int MaximumTitleBytes = 256;

    /// <summary>Maximum UTF-8 bytes in a body, the bound the issue adapter already uses.</summary>
    public const int MaximumBodyBytes = 4096;

    /// <summary>
    /// The confidence values a triage report can record. A description states one of these or is not
    /// composed at all; there is no default and no "unknown", because a description that quietly
    /// omitted the uncertainty would be the one failure this section exists to prevent.
    /// </summary>
    private static readonly string[] KnownConfidence = ["Low", "Medium", "High"];

    public static bool IsKnownConfidence(string? value) =>
        value is not null && Array.IndexOf(KnownConfidence, value) >= 0;

    public static string Title(Guid originReportId) =>
        "Governed remediation for incident report " + originReportId.ToString("N");

    public static string Body(PullRequestPayload payload)
    {
        var builder = new StringBuilder(
            "This description is composed by IncidentCompass. No part of it was written by a language " +
            "model, and it renders no file path, no source line, no prompt and no credential.\n\n");
        AppendOrigin(builder, payload);
        AppendChange(builder, payload);
        AppendUncertainty(builder, payload);
        builder.Append(
            "## Test evidence\n\n" +
            "No test was executed. This release starts no process and runs no test command, so nothing " +
            "has checked that this change compiles, builds or behaves. There is no test evidence to " +
            "cite because none exists. Do not merge this on the strength of this description.\n");
        return builder.ToString();
    }

    private static void AppendOrigin(StringBuilder builder, PullRequestPayload payload)
    {
        builder.Append("## Origin\n\n")
            .Append("- Incident report: `").Append(payload.OriginReportId.ToString("N")).Append("`\n")
            .Append("- Originating issue: ");
        if (payload.IssueNumber > 0)
        {
            builder.Append('#').Append(Number(payload.IssueNumber)).Append('\n');
        }
        else
        {
            builder.Append("none. The report cited no existing ticket in this repository.\n");
        }

        builder.Append("- Approved branch-push action: `")
            .Append(payload.PredecessorActionId.ToString("N")).Append("`\n\n");
    }

    private static void AppendChange(StringBuilder builder, PullRequestPayload payload)
    {
        builder.Append("## Change\n\n")
            .Append("- Head commit: `").Append(payload.HeadCommitSha).Append("`\n")
            .Append("- Base commit: `").Append(payload.BaseCommitSha).Append("`\n")
            .Append("- File sections changed: ").Append(Number(payload.FilesChanged)).Append('\n')
            .Append("- Approved base tree identity: `").Append(payload.BaseTreeIdentity).Append("`\n")
            .Append("- Resulting tree identity: `").Append(payload.ResultTreeIdentity).Append("`\n")
            .Append("- Correspondence digest: `").Append(payload.CorrespondenceDigest).Append("`\n")
            .Append("- Paths proved byte-identical to the approved base: ")
            .Append(Number(payload.ProvedPathCount)).Append('\n')
            .Append("- Local-only paths excluded from the push: ")
            .Append(Number(payload.ExcludedPathCount)).Append("\n\n");
    }

    private static void AppendUncertainty(StringBuilder builder, PullRequestPayload payload)
    {
        builder.Append("## Uncertainty\n\n")
            .Append("- The incident report this change answers records **")
            .Append(payload.ReportConfidence)
            .Append("** confidence in its own analysis. The change is no more certain than that.\n")
            .Append("- The change was written by a language model from the report and its cited ")
            .Append("evidence. A person approved applying it and publishing it; nobody has stated ")
            .Append("that it is correct.\n")
            .Append("- Byte-equivalence to the approved base was proved only over the paths that base ")
            .Append("and the base commit above have in common. Paths present only on the local side ")
            .Append("were excluded rather than published, and the counts above say how many of each.\n\n");
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
