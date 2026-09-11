using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.Tools;

namespace IncidentCompass.Application.Remediation;

/// <summary>
/// Builds and reads the one frozen shape a remediation diff is approved in.
/// </summary>
/// <remarks>
/// <para>
/// <b>One writer, three readers, and why they are the same code.</b> The publisher builds the
/// arguments and measures what they would become; the external-action adapter validates those
/// arguments and prepares the canonical payload the approval contract hashes; and the dispatcher
/// reads that payload back when an approved action executes. If any two of those disagreed by a byte
/// the approval would be over something other than what runs, so all three go through this type and
/// the payload is rebuilt from the arguments rather than carried alongside them.
/// </para>
/// <para>
/// <b>Why the payload is not simply the arguments.</b> The arguments are what a caller states; the
/// payload is what the backend concluded, and it adds the three things a caller is never allowed to
/// state: the schema version, that no test command ran, and the sentence saying what that means.
/// Building them here rather than accepting them means no caller, and certainly no model, can supply
/// a payload that claims a test ran.
/// </para>
/// </remarks>
internal static class RemediationProposalPayloadFactory
{
    /// <summary>
    /// The statement that travels in the frozen bytes. It is a payload field rather than only a
    /// review summary because the summary is not covered by the approval hash and this is: a person
    /// approving this proposal echoes a hash computed over these exact words.
    /// </summary>
    public const string TestStatement =
        "No test was executed. This release starts no process and runs no test command, so nothing " +
        "has checked that this change builds or behaves. Approving it approves an untested change.";

    private const string EvidenceDomain = "IncidentCompass.RemediationEvidence.v1";
    private const string ProposalKeyPrefix = "post-report:v1:";
    private const int SchemaVersion = 1;
    private const int MaximumNameCharacters = 128;
    private const int MaximumFileSections = 1024;
    private const int MaximumEvidenceCount = 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// The idempotency key one report's <c>code_write</c> proposal is created under, in the shape
    /// every other post-report proposal uses. It names the report and the tool and nothing else, so
    /// one report has at most one such proposal however many times the workflow is evaluated.
    /// </summary>
    public static string ProposalKey(Guid originReportId) =>
        $"{ProposalKeyPrefix}{originReportId:N}:{RemediationApplyToolDescriptor.ToolId}";

    /// <summary>
    /// The identity of the cited source evidence a change was derived from: a digest over the
    /// artifact ids, ordered so that the same set always produces the same value.
    /// </summary>
    public static string ComputeEvidenceSha256(IEnumerable<Guid> artifactIds)
    {
        var builder = new StringBuilder(EvidenceDomain);
        foreach (var id in artifactIds.Distinct().OrderBy(static id => id))
        {
            builder.Append('\n').Append(id.ToString("D"));
        }

        return Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(builder.ToString())));
    }

    /// <summary>
    /// The governed facts, in the shape the proposal command carries them.
    /// </summary>
    public static JsonElement BuildArguments(RemediationProposalPayload payload) =>
        CanonicalJsonSerializer.ToElement(WriteArguments(payload));

    /// <summary>
    /// Reads stated arguments, refusing anything that is not exactly one governed fact set.
    /// </summary>
    public static RemediationProposalPayload? TryReadArguments(JsonElement arguments) =>
        TryRead(arguments, payloadForm: false);

    /// <summary>
    /// Reads a frozen payload back, refusing anything an approval could not have been taken over.
    /// </summary>
    public static RemediationProposalPayload? TryReadPayload(ReadOnlySpan<byte> canonicalPayload)
    {
        try
        {
            using var document = JsonDocument.Parse(canonicalPayload.ToArray());
            return TryRead(document.RootElement, payloadForm: true);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Freezes one diff: the canonical payload an approval hashes, and the summary a person reads.
    /// </summary>
    public static ExternalActionPreparation Create(RemediationProposalPayload payload)
    {
        var body = WriteArguments(payload);
        body["schemaVersion"] = SchemaVersion;
        body["testCommandId"] = null;
        body["testStatement"] = TestStatement;
        return new ExternalActionPreparation(
            StrictUtf8.GetBytes(CanonicalJsonSerializer.Canonicalize(body)),
            BuildReviewSummary(payload));
    }

    private static JsonObject WriteArguments(RemediationProposalPayload payload) => new()
    {
        ["baseTreeIdentity"] = payload.BaseTreeIdentity,
        ["evidenceCount"] = payload.EvidenceCount,
        ["evidenceSha256"] = payload.EvidenceSha256,
        ["filesChanged"] = payload.FilesChanged,
        ["originReportId"] = payload.OriginReportId.ToString("N"),
        ["patch"] = payload.PatchText,
        ["patchBytes"] = payload.PatchBytes,
        ["release"] = payload.Release,
        ["resultTreeIdentity"] = payload.ResultTreeIdentity,
        ["serviceName"] = payload.ServiceName,
        ["testOutcome"] = payload.TestOutcome
    };

    /// <summary>
    /// What a reviewer sees in the approval list before they open anything. It leads with the one
    /// thing that is easiest to assume and wrong to assume, and ends with the limit of what approving
    /// authorizes.
    /// </summary>
    private static string BuildReviewSummary(RemediationProposalPayload payload)
    {
        var builder = new StringBuilder(
            "UNTESTED CHANGE: no test was executed. Approve only if you accept an unverified change. ");
        builder.Append("Diff for service '").Append(payload.ServiceName)
            .Append("' at release '").Append(payload.Release).Append("': ")
            .Append(payload.FilesChanged.ToString(CultureInfo.InvariantCulture)).Append(" file section(s), ")
            .Append(payload.PatchBytes.ToString(CultureInfo.InvariantCulture)).Append(" bytes, prepared against base ")
            .Append(payload.BaseTreeIdentity[..12]).Append(" and producing tree ")
            .Append(payload.ResultTreeIdentity[..12]).Append(", derived from ")
            .Append(payload.EvidenceCount.ToString(CultureInfo.InvariantCulture))
            .Append(" cited source artifact(s) (").Append(payload.EvidenceSha256[..12]).Append("). ")
            .Append("Approving authorizes re-applying exactly these bytes to a disposable copy of that base ")
            .Append("and nothing else: no branch is pushed, no pull request is opened and nothing is merged.");
        return builder.ToString();
    }

    private static RemediationProposalPayload? TryRead(JsonElement root, bool payloadForm)
    {
        if (root.ValueKind != JsonValueKind.Object || !HasExactProperties(root, payloadForm) ||
            !TryReadReportId(root, out var reportId) ||
            !TryReadName(root, "serviceName", out var serviceName) ||
            !TryReadName(root, "release", out var release) ||
            !TryReadIdentity(root, "baseTreeIdentity", out var baseTree) ||
            !TryReadIdentity(root, "resultTreeIdentity", out var resultTree) ||
            !TryReadIdentity(root, "evidenceSha256", out var evidence) ||
            !TryReadCount(root, "filesChanged", MaximumFileSections, out var filesChanged) ||
            !TryReadCount(root, "evidenceCount", MaximumEvidenceCount, out var evidenceCount) ||
            !TryReadPatch(root, out var patch, out var patchBytes) ||
            !IsNotExecuted(root, payloadForm))
        {
            return null;
        }

        return new RemediationProposalPayload(
            reportId, serviceName, release, baseTree, resultTree,
            filesChanged, patchBytes, patch, evidence, evidenceCount,
            RemediationDiff.TestNotExecuted);
    }

    private static bool HasExactProperties(JsonElement root, bool payloadForm)
    {
        var expected = payloadForm ? 14 : 11;
        return root.EnumerateObject().Count() == expected &&
            root.EnumerateObject().All(property => property.Name is
                "baseTreeIdentity" or "evidenceCount" or "evidenceSha256" or "filesChanged" or
                "originReportId" or "patch" or "patchBytes" or "release" or "resultTreeIdentity" or
                "serviceName" or "testOutcome" ||
                (payloadForm && property.Name is "schemaVersion" or "testCommandId" or "testStatement"));
    }

    /// <summary>
    /// The one check that keeps an untested artifact and a tested one from being confused. In the
    /// payload form it also requires the two fields a caller never supplies, so a payload that says
    /// nothing ran cannot have been written by anything but this type.
    /// </summary>
    private static bool IsNotExecuted(JsonElement root, bool payloadForm)
    {
        if (!root.TryGetProperty("testOutcome", out var outcome) ||
            outcome.ValueKind != JsonValueKind.String ||
            !string.Equals(outcome.GetString(), RemediationDiff.TestNotExecuted, StringComparison.Ordinal))
        {
            return false;
        }

        return !payloadForm ||
            (root.GetProperty("testCommandId").ValueKind == JsonValueKind.Null &&
             root.GetProperty("schemaVersion").TryGetInt32(out var version) && version == SchemaVersion &&
             root.GetProperty("testStatement").ValueKind == JsonValueKind.String &&
             string.Equals(root.GetProperty("testStatement").GetString(), TestStatement, StringComparison.Ordinal));
    }

    private static bool TryReadReportId(JsonElement root, out Guid value)
    {
        value = Guid.Empty;
        return root.TryGetProperty("originReportId", out var property) &&
            property.ValueKind == JsonValueKind.String &&
            Guid.TryParseExact(property.GetString(), "N", out value) &&
            value != Guid.Empty &&
            string.Equals(property.GetString(), value.ToString("N"), StringComparison.Ordinal);
    }

    private static bool TryReadName(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()!;
        return !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumNameCharacters &&
            !value.Any(char.IsControl);
    }

    private static bool TryReadIdentity(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()!;
        return ActionProposalValidator.IsLowerHexSha256(value);
    }

    private static bool TryReadCount(JsonElement root, string name, int maximum, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out value) && value >= 1 && value <= maximum;
    }

    /// <summary>
    /// The diff itself, with its own measured length beside it. The length is required to agree,
    /// which is what makes <c>patchBytes</c> a statement about these bytes rather than a number a
    /// caller chose.
    /// </summary>
    private static bool TryReadPatch(JsonElement root, out string patch, out int patchBytes)
    {
        patch = string.Empty;
        patchBytes = 0;
        if (!root.TryGetProperty("patch", out var property) || property.ValueKind != JsonValueKind.String ||
            !TryReadCount(root, "patchBytes", ActionApprovalLimits.MaximumPayloadBytes, out patchBytes))
        {
            return false;
        }

        patch = property.GetString()!;
        return StrictUtf8.GetByteCount(patch) == patchBytes;
    }
}
