using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.Tools;

namespace IncidentCompass.Application.Remediation;

/// <summary>
/// Builds and reads the one frozen shape a branch push is approved in.
/// </summary>
/// <remarks>
/// One writer and three readers, for the same reason the <c>code_write</c> factory has them: the
/// publisher builds the arguments, the adapter validates them and prepares the canonical payload the
/// approval contract hashes, and the dispatch reads that payload back. If any two disagreed by a byte
/// the approval would be over something other than what runs, so all three go through this type and
/// the payload is rebuilt from the arguments rather than carried beside them.
/// </remarks>
internal static class BranchPushPayloadFactory
{
    /// <summary>
    /// What approving a push authorizes and what it does not, in the frozen bytes rather than only in
    /// a review summary, because a person approving echoes a hash computed over these exact words.
    /// </summary>
    public const string PublicationStatement =
        "Approving creates one new branch at one new commit in the configured repository. No existing " +
        "reference is moved or deleted, nothing is merged, no pull request is opened, no repository " +
        "setting is changed, and no test is executed. Executing it does schedule one pull-request " +
        "proposal, which is a separate decision a person has to approve and which an operator can " +
        "switch off entirely; nothing about opening a pull request is automatic.";

    private const string ProposalKeyPrefix = "post-report:v1:";
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ";
    /// <summary>
    /// Version 2. The frozen statement about what approving a push sets in motion changed when a push
    /// began scheduling a pull-request proposal, and a statement inside the approval hash is not a
    /// comment: a payload frozen under version 1 says something that is no longer the whole truth. The
    /// version is bumped rather than left alone so that such a payload is refused for a stated reason
    /// instead of failing an opaque text comparison. The cost is that a push proposed before an upgrade
    /// and still awaiting approval after one is no longer executable and needs a fresh proposal, which
    /// the workflow produces on the next evaluation.
    /// </summary>
    private const int SchemaVersion = 2;

    private const int ArgumentPropertyCount = 19;
    private const int PayloadPropertyCount = ArgumentPropertyCount + 4;
    private const int MaximumNameCharacters = 128;
    private const int MaximumRepositoryCharacters = 140;
    private const int MaximumFileSections = 1024;
    private const int MaximumPathCount = 1_000_000;

    /// <summary>
    /// The idempotency key one report's push proposal is created under, in the shape every other
    /// post-report proposal uses. One report has at most one push proposal, however many times the
    /// workflow is evaluated.
    /// </summary>
    public static string ProposalKey(Guid originReportId) =>
        $"{ProposalKeyPrefix}{originReportId:N}:{BranchPushToolDescriptor.ToolId}";

    public static JsonElement BuildArguments(BranchPushPayload payload) =>
        CanonicalJsonSerializer.ToElement(WriteArguments(payload));

    public static BranchPushPayload? TryReadArguments(JsonElement arguments) =>
        TryRead(arguments, payloadForm: false);

    public static BranchPushPayload? TryReadPayload(ReadOnlySpan<byte> canonicalPayload)
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

    public static ExternalActionPreparation Create(BranchPushPayload payload)
    {
        var body = WriteArguments(payload);
        body["landedStatement"] = PublicationStatement;
        body["schemaVersion"] = SchemaVersion;
        body["testCommandId"] = null;
        body["testStatement"] = RemediationProposalPayloadFactory.TestStatement;
        return new ExternalActionPreparation(
            RemediationPayloadFields.Utf8Bytes(CanonicalJsonSerializer.Canonicalize(body)),
            BuildReviewSummary(payload));
    }

    public static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static JsonObject WriteArguments(BranchPushPayload payload) => new()
    {
        ["baseBranch"] = payload.BaseBranch,
        ["baseCommitSha"] = payload.BaseCommitSha,
        ["baseTreeIdentity"] = payload.BaseTreeIdentity,
        ["baseTreeSha"] = payload.BaseTreeSha,
        ["branchName"] = payload.BranchName,
        ["commitTimestamp"] = FormatTimestamp(payload.CommitTimestampUtc),
        ["correspondenceDigest"] = payload.CorrespondenceDigest,
        ["excludedPathCount"] = payload.ExcludedPathCount,
        ["filesChanged"] = payload.FilesChanged,
        ["originReportId"] = payload.OriginReportId.ToString("N"),
        ["patch"] = payload.PatchText,
        ["patchBytes"] = payload.PatchBytes,
        ["predecessorActionId"] = payload.PredecessorActionId.ToString("N"),
        ["predecessorResultSha256"] = payload.PredecessorResultSha256,
        ["provedPathCount"] = payload.ProvedPathCount,
        ["release"] = payload.Release,
        ["repository"] = payload.Repository,
        ["resultTreeIdentity"] = payload.ResultTreeIdentity,
        ["serviceName"] = payload.ServiceName
    };

    /// <summary>
    /// What a reviewer sees before they open anything. It leads with the two things that are easiest
    /// to assume and wrong to assume - that something checked the change, and that the base is
    /// whatever the branch looks like today - and ends with the limit of what approving authorizes.
    /// </summary>
    private static string BuildReviewSummary(BranchPushPayload payload)
    {
        var builder = new StringBuilder(
            "UNTESTED CHANGE: no test was executed. Approve only if you accept an unverified change. ");
        builder.Append("Create branch '").Append(payload.BranchName)
            .Append("' in ").Append(payload.Repository)
            .Append(" from '").Append(payload.BaseBranch)
            .Append("' at commit ").Append(payload.BaseCommitSha[..12])
            .Append(", carrying ").Append(Count(payload.FilesChanged))
            .Append(" changed file section(s) for service '").Append(payload.ServiceName)
            .Append("' at release '").Append(payload.Release).Append("'. ")
            .Append("That commit was proved byte-identical to the approved base tree ")
            .Append(payload.BaseTreeIdentity[..12]).Append(" over ")
            .Append(Count(payload.ProvedPathCount)).Append(" path(s), with ")
            .Append(Count(payload.ExcludedPathCount))
            .Append(" local-only path(s) excluded from the push (").Append(payload.CorrespondenceDigest[..12])
            .Append("). ").Append(PublicationStatement);
        return builder.ToString();
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static BranchPushPayload? TryRead(JsonElement root, bool payloadForm)
    {
        if (root.ValueKind != JsonValueKind.Object || !HasExactProperties(root, payloadForm) ||
            !RemediationPayloadFields.TryReadReportId(root, "originReportId", out var reportId) ||
            !RemediationPayloadFields.TryReadReportId(root, "predecessorActionId", out var predecessor) ||
            !RemediationPayloadFields.TryReadName(root, "serviceName", MaximumNameCharacters, out var service) ||
            !RemediationPayloadFields.TryReadName(root, "release", MaximumNameCharacters, out var release) ||
            !RemediationPayloadFields.TryReadName(root, "repository", MaximumRepositoryCharacters, out var repository) ||
            !RemediationPayloadFields.TryReadName(root, "baseBranch", MaximumNameCharacters, out var baseBranch) ||
            !RemediationPayloadFields.TryReadName(root, "branchName", RemediationBranchName.Characters, out var branch) ||
            !RemediationBranchName.IsDerived(branch, reportId) ||
            !RemediationPayloadFields.TryReadGitObjectName(root, "baseCommitSha", out var baseCommit) ||
            !RemediationPayloadFields.TryReadGitObjectName(root, "baseTreeSha", out var baseTree) ||
            !RemediationPayloadFields.TryReadIdentity(root, "baseTreeIdentity", out var baseIdentity) ||
            !RemediationPayloadFields.TryReadIdentity(root, "resultTreeIdentity", out var resultIdentity) ||
            !RemediationPayloadFields.TryReadIdentity(root, "correspondenceDigest", out var digest) ||
            !RemediationPayloadFields.TryReadIdentity(root, "predecessorResultSha256", out var predecessorResult) ||
            !RemediationPayloadFields.TryReadCount(root, "filesChanged", 1, MaximumFileSections, out var filesChanged) ||
            !RemediationPayloadFields.TryReadCount(root, "provedPathCount", 1, MaximumPathCount, out var proved) ||
            !RemediationPayloadFields.TryReadCount(root, "excludedPathCount", 0, MaximumPathCount, out var excluded) ||
            !TryReadPatch(root, out var patch, out var patchBytes) ||
            !TryReadTimestamp(root, out var timestamp) ||
            !HasBackendStatements(root, payloadForm))
        {
            return null;
        }

        return new BranchPushPayload(
            reportId, service, release, repository, baseBranch, branch, baseCommit, baseTree,
            baseIdentity, resultIdentity, filesChanged, patchBytes, patch, digest, proved, excluded,
            predecessor, predecessorResult, timestamp);
    }

    private static bool HasExactProperties(JsonElement root, bool payloadForm)
    {
        var expected = payloadForm ? PayloadPropertyCount : ArgumentPropertyCount;
        return root.EnumerateObject().Count() == expected &&
            root.EnumerateObject().All(property => IsArgumentProperty(property.Name) ||
                (payloadForm && property.Name is
                    "landedStatement" or "schemaVersion" or "testCommandId" or "testStatement"));
    }

    private static bool IsArgumentProperty(string name) => name is
        "baseBranch" or "baseCommitSha" or "baseTreeIdentity" or "baseTreeSha" or "branchName" or
        "commitTimestamp" or "correspondenceDigest" or "excludedPathCount" or "filesChanged" or
        "originReportId" or "patch" or "patchBytes" or "predecessorActionId" or
        "predecessorResultSha256" or "provedPathCount" or "release" or "repository" or
        "resultTreeIdentity" or "serviceName";

    /// <summary>
    /// The three sentences a caller never supplies. A payload that says a push landed nothing but a
    /// branch, and that no test ran, cannot have been written by anything but this type.
    /// </summary>
    private static bool HasBackendStatements(JsonElement root, bool payloadForm) =>
        !payloadForm ||
        (root.GetProperty("testCommandId").ValueKind == JsonValueKind.Null &&
         root.GetProperty("schemaVersion").TryGetInt32(out var version) && version == SchemaVersion &&
         IsExactly(root, "testStatement", RemediationProposalPayloadFactory.TestStatement) &&
         IsExactly(root, "landedStatement", PublicationStatement));

    private static bool IsExactly(JsonElement root, string name, string expected) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
        string.Equals(property.GetString(), expected, StringComparison.Ordinal);

    private static bool TryReadPatch(JsonElement root, out string patch, out int patchBytes)
    {
        patch = string.Empty;
        patchBytes = 0;
        if (!root.TryGetProperty("patch", out var property) || property.ValueKind != JsonValueKind.String ||
            !RemediationPayloadFields.TryReadCount(
                root, "patchBytes", 1, ActionApprovalLimits.MaximumPayloadBytes, out patchBytes))
        {
            return false;
        }

        patch = property.GetString()!;
        return RemediationPayloadFields.Utf8ByteCount(patch) == patchBytes;
    }

    /// <summary>
    /// The pinned commit instant, required to be exactly the text this type writes. Accepting any
    /// other spelling of the same moment would let two payloads that hash differently describe the
    /// same commit, and the approval hash would stop being a name for one thing.
    /// </summary>
    private static bool TryReadTimestamp(JsonElement root, out DateTimeOffset value)
    {
        value = default;
        if (!root.TryGetProperty("commitTimestamp", out var property) ||
            property.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParseExact(
                property.GetString(), TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value))
        {
            return false;
        }

        return string.Equals(FormatTimestamp(value), property.GetString(), StringComparison.Ordinal);
    }
}
