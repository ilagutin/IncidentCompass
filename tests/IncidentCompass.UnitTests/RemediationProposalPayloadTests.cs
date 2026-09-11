using System.Text;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Infrastructure.SourceContext;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The bytes a person approves when they approve a remediation diff.
/// </summary>
/// <remarks>
/// These are worth their own tests because the payload is the approval: the contract hashes it, a
/// reviewer echoes that hash back, and the adapter reads the same bytes when the action runs. A field
/// silently dropped from it would not break anything visible; it would quietly remove something from
/// what the approval covers. So the shape is asserted exactly, and the hash is asserted to move when
/// any part of it moves.
/// </remarks>
public sealed class RemediationProposalPayloadTests
{
    private const string Patch = "--- a/src/Checkout.cs\n+++ b/src/Checkout.cs\n@@ -1,1 +1,1 @@\n-old\n+new\n";
    private static readonly Guid ReportId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>
    /// Exactly the fourteen fields, and no fifteenth. The count is asserted as well as the names,
    /// because a payload gaining a field changes what every future approval hash covers.
    /// </summary>
    [Fact]
    public void Create_FreezesTheDiffTheBaseTheBindingAndTheEvidenceIdentity()
    {
        var payload = Payload();

        var frozen = JsonNode.Parse(
            Encoding.UTF8.GetString(RemediationProposalPayloadFactory.Create(payload).CanonicalPayload))!.AsObject();

        Assert.Equal(14, frozen.Count);
        Assert.Equal(Patch, frozen["patch"]!.GetValue<string>());
        Assert.Equal(payload.BaseTreeIdentity, frozen["baseTreeIdentity"]!.GetValue<string>());
        Assert.Equal(payload.ResultTreeIdentity, frozen["resultTreeIdentity"]!.GetValue<string>());
        Assert.Equal("checkout-api", frozen["serviceName"]!.GetValue<string>());
        Assert.Equal("1.4", frozen["release"]!.GetValue<string>());
        Assert.Equal(payload.EvidenceSha256, frozen["evidenceSha256"]!.GetValue<string>());
        Assert.Equal(2, frozen["evidenceCount"]!.GetValue<int>());
        Assert.Equal(ReportId.ToString("N"), frozen["originReportId"]!.GetValue<string>());
        Assert.Equal(Encoding.UTF8.GetByteCount(Patch), frozen["patchBytes"]!.GetValue<int>());
        Assert.Equal(1, frozen["schemaVersion"]!.GetValue<int>());
    }

    /// <summary>
    /// The statement that no test ran is in the frozen bytes, in words, beside the machine-readable
    /// outcome and the absent command id.
    /// </summary>
    /// <remarks>
    /// It is in the payload rather than only in the review summary because the summary is not part of
    /// the approval hash and the payload is: a person approving echoes a hash computed over these
    /// exact words, so the claim cannot be edited away from under an approval that was already given.
    /// </remarks>
    [Fact]
    public void Create_SaysInTheFrozenBytesThatNoTestWasExecuted()
    {
        var canonical = Encoding.UTF8.GetString(
            RemediationProposalPayloadFactory.Create(Payload()).CanonicalPayload);

        Assert.Contains("\"testOutcome\":\"not_executed\"", canonical, StringComparison.Ordinal);
        Assert.Contains("\"testCommandId\":null", canonical, StringComparison.Ordinal);
        Assert.Contains("No test was executed.", canonical, StringComparison.Ordinal);
        Assert.Contains("approves an untested change", canonical, StringComparison.Ordinal);
    }

    /// <summary>
    /// The summary a reviewer reads before opening anything leads with the same thing.
    /// </summary>
    [Fact]
    public void Create_LeadsTheReviewSummaryWithTheUntestedWarningAndTheLimitOfTheApproval()
    {
        var summary = RemediationProposalPayloadFactory.Create(Payload()).ReviewSummary;

        Assert.StartsWith("UNTESTED CHANGE: no test was executed.", summary, StringComparison.Ordinal);
        Assert.Contains("no branch is pushed", summary, StringComparison.Ordinal);
        Assert.Contains("nothing is merged", summary, StringComparison.Ordinal);
        Assert.InRange(Encoding.UTF8.GetByteCount(summary), 1, ActionApprovalLimits.MaximumSummaryBytes);
    }

    /// <summary>
    /// A payload that says something about a test other than "nothing ran" cannot be read back, so an
    /// approval can never be taken over one and an approved one can never be executed.
    /// </summary>
    [Theory]
    [InlineData("testOutcome", "\"passed\"")]
    [InlineData("testOutcome", "\"failed\"")]
    [InlineData("testCommandId", "\"dotnet-test\"")]
    [InlineData("testStatement", "\"All tests passed.\"")]
    [InlineData("schemaVersion", "2")]
    public void TryReadPayload_RefusesAPayloadThatSaysAnythingElseAboutATest(string field, string replacement)
    {
        var tampered = Tamper(field, replacement);

        Assert.Null(RemediationProposalPayloadFactory.TryReadPayload(tampered));
    }

    /// <summary>
    /// A frozen payload reads back as the same facts it was built from, which is what lets the
    /// adapter apply the approved bytes rather than a re-derivation of them.
    /// </summary>
    [Fact]
    public void TryReadPayload_ReadsBackExactlyWhatWasFrozen()
    {
        var payload = Payload();

        var read = RemediationProposalPayloadFactory.TryReadPayload(
            RemediationProposalPayloadFactory.Create(payload).CanonicalPayload);

        Assert.Equal(payload, read);
    }

    /// <summary>
    /// Stated arguments are the governed fact set and nothing else: no extra field, no missing one,
    /// no identity that is not a digest, and no byte count that disagrees with the bytes.
    /// </summary>
    [Theory]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("identity-not-a-digest")]
    [InlineData("byte-count-disagrees")]
    [InlineData("payload-fields-stated")]
    public void TryReadArguments_RefusesAnythingButTheExactGovernedFactSet(string variant)
    {
        var arguments = JsonNode.Parse(
            RemediationProposalPayloadFactory.BuildArguments(Payload()).GetRawText())!.AsObject();
        switch (variant)
        {
            case "extra":
                arguments["repository"] = "git@example.test:someone/else.git";
                break;
            case "missing":
                arguments.Remove("baseTreeIdentity");
                break;
            case "identity-not-a-digest":
                arguments["baseTreeIdentity"] = "HEAD";
                break;
            case "byte-count-disagrees":
                arguments["patchBytes"] = Encoding.UTF8.GetByteCount(Patch) + 1;
                break;
            default:
                arguments["testStatement"] = RemediationProposalPayloadFactory.TestStatement;
                break;
        }

        Assert.Null(RemediationProposalPayloadFactory.TryReadArguments(
            CanonicalJsonSerializer.ToElement(arguments)));
    }

    /// <summary>
    /// The idempotency key names the report and the tool, so one report has at most one such
    /// proposal however many times the workflow is evaluated.
    /// </summary>
    [Fact]
    public void ProposalKey_NamesTheReportAndTheToolAndNothingElse()
    {
        var key = RemediationProposalPayloadFactory.ProposalKey(ReportId);

        Assert.Equal($"post-report:v1:{ReportId:N}:remediation_apply", key);
        Assert.InRange(key.Length, 1, ActionApprovalLimits.MaximumProposalKeyCharacters);
        Assert.Equal(key, RemediationProposalPayloadFactory.ProposalKey(ReportId));
    }

    /// <summary>
    /// The evidence identity depends on the set and not on the order it was read in, and it moves
    /// when the set does.
    /// </summary>
    [Fact]
    public void ComputeEvidenceSha256_DependsOnTheSetAndNotOnItsOrder()
    {
        var first = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        var second = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

        Assert.Equal(
            RemediationProposalPayloadFactory.ComputeEvidenceSha256([first, second]),
            RemediationProposalPayloadFactory.ComputeEvidenceSha256([second, first]));
        Assert.NotEqual(
            RemediationProposalPayloadFactory.ComputeEvidenceSha256([first, second]),
            RemediationProposalPayloadFactory.ComputeEvidenceSha256([first]));
    }

    /// <summary>
    /// Every part of what decides the resulting tree moves the approval hash.
    /// </summary>
    /// <remarks>
    /// This is the claim the item asks for, made testable. The diff, the base it applies to, the
    /// checkout the base belongs to, the evidence it was derived from and the host binding are varied
    /// one at a time; each has to produce a different approval hash, or that part is not covered and
    /// an approval could be reused across a change to it.
    /// </remarks>
    [Theory]
    [InlineData("patch")]
    [InlineData("base")]
    [InlineData("result")]
    [InlineData("service")]
    [InlineData("release")]
    [InlineData("evidence")]
    [InlineData("binding")]
    [InlineData("tool")]
    public void ApprovalHash_MovesWhenAnythingThatDecidesTheTreeMoves(string varied)
    {
        var baseline = ApprovalHash(Payload(), new string('c', 64), RemediationApplyToolDescriptor.ToolId);
        var payload = Payload();
        var binding = new string('c', 64);
        var toolId = RemediationApplyToolDescriptor.ToolId;
        switch (varied)
        {
            case "patch": payload = payload with { PatchText = Patch + " ", PatchBytes = payload.PatchBytes + 1 }; break;
            case "base": payload = payload with { BaseTreeIdentity = new string('9', 64) }; break;
            case "result": payload = payload with { ResultTreeIdentity = new string('9', 64) }; break;
            case "service": payload = payload with { ServiceName = "another-service" }; break;
            case "release": payload = payload with { Release = "1.5" }; break;
            case "evidence": payload = payload with { EvidenceSha256 = new string('9', 64) }; break;
            case "binding": binding = new string('d', 64); break;
            default: toolId = "ticket_create"; break;
        }

        Assert.NotEqual(baseline, ApprovalHash(payload, binding, toolId));
    }

    /// <summary>
    /// A diff at the raw budget still fits the ceiling once the envelope this payload actually uses
    /// is around it, not the three-field envelope the budget was derived against.
    /// </summary>
    /// <remarks>
    /// The derivation reserves a fixed 1 KiB for whatever travels beside the diff. This payload uses
    /// more of that reserve than the derivation's own example does, so the reserve is checked here
    /// against the real envelope. The publisher measures it again per proposal and refuses, because
    /// a service name made entirely of characters the canonical writer escapes to six bytes each can
    /// still exceed what any fixed reserve can promise.
    /// </remarks>
    [Fact]
    public void Create_LeavesAWorstCaseDiffInsideTheActionPayloadCeiling()
    {
        var payload = Payload() with
        {
            PatchText = new string('<', SourcePatchLimits.RawBudgetBytes),
            PatchBytes = SourcePatchLimits.RawBudgetBytes
        };

        Assert.InRange(
            RemediationProposalPayloadFactory.Create(payload).CanonicalPayload.Length,
            1,
            ActionApprovalLimits.MaximumPayloadBytes);
    }

    private static string ApprovalHash(RemediationProposalPayload payload, string binding, string toolId)
    {
        var canonical = RemediationProposalPayloadFactory.Create(payload).CanonicalPayload;
        return ActionApprovalContractV1.ComputeApprovalSha256(
            payload.OriginReportId,
            toolId,
            ActionCategory.CodeWrite,
            ActionExecutionMode.Live,
            RemediationApplyToolDescriptor.LogicalTargetId,
            binding,
            ActionApprovalContractV1.ComputePayloadSha256(canonical),
            canonical,
            new string('e', 64));
    }

    private static byte[] Tamper(string field, string replacement)
    {
        var frozen = JsonNode.Parse(
            Encoding.UTF8.GetString(RemediationProposalPayloadFactory.Create(Payload()).CanonicalPayload))!.AsObject();
        frozen[field] = JsonNode.Parse(replacement);
        return Encoding.UTF8.GetBytes(CanonicalJsonSerializer.Canonicalize(frozen));
    }

    private static RemediationProposalPayload Payload() => new(
        ReportId,
        "checkout-api",
        "1.4",
        new string('a', 64),
        new string('b', 64),
        FilesChanged: 1,
        Encoding.UTF8.GetByteCount(Patch),
        Patch,
        RemediationProposalPayloadFactory.ComputeEvidenceSha256(
            [Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
             Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002")]),
        EvidenceCount: 2,
        RemediationDiff.TestNotExecuted);
}
