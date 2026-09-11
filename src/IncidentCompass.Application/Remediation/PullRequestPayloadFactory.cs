using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.Tools;

namespace IncidentCompass.Application.Remediation;

/// <summary>
/// Builds and reads the one frozen shape a pull request is approved in.
/// </summary>
/// <remarks>
/// One writer and three readers, for the same reason the <c>branch_push</c> factory has them: the
/// publisher builds the arguments, the adapter validates them and prepares the canonical payload the
/// approval contract hashes, and the dispatch reads that payload back. If any two disagreed by a byte
/// the approval would be over something other than what runs, so all three go through this type and the
/// payload is rebuilt from the arguments rather than carried beside them.
/// </remarks>
internal static class PullRequestPayloadFactory
{
    /// <summary>
    /// What approving a pull request authorizes and what it does not, in the frozen bytes rather than
    /// only in a review summary, because a person approving echoes a hash computed over these exact
    /// words.
    /// </summary>
    public const string PublicationStatement =
        "Approving opens one pull request from the approved head branch into the configured base " +
        "branch and nothing else. Nothing is merged, no automatic merge is enabled or scheduled, no " +
        "reference is moved or deleted, no repository setting is changed, and no test is executed. " +
        "Whether the change is ever merged stays a decision people make in the repository.";

    private const string ProposalKeyPrefix = "post-report:v1:";
    private const int SchemaVersion = 1;
    private const int ArgumentPropertyCount = 18;
    private const int PayloadPropertyCount = ArgumentPropertyCount + 6;
    private const int MaximumNameCharacters = 128;
    private const int MaximumRepositoryCharacters = 140;
    private const int MaximumFileSections = 1024;
    private const int MaximumPathCount = 1_000_000;

    /// <summary>Largest issue number this release will render, in the provider's own integer shape.</summary>
    private const int MaximumIssueNumber = 1_000_000_000;

    /// <summary>
    /// The idempotency key one report's pull-request proposal is created under, in the shape every
    /// other post-report proposal uses. One report has at most one pull-request proposal, however many
    /// times the workflow is evaluated.
    /// </summary>
    public static string ProposalKey(Guid originReportId) =>
        $"{ProposalKeyPrefix}{originReportId:N}:{PullRequestToolDescriptor.ToolId}";

    public static JsonElement BuildArguments(PullRequestPayload payload) =>
        CanonicalJsonSerializer.ToElement(WriteArguments(payload));

    public static PullRequestPayload? TryReadArguments(JsonElement arguments) =>
        TryRead(arguments, payloadForm: false);

    public static PullRequestPayload? TryReadPayload(ReadOnlySpan<byte> canonicalPayload)
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

    public static ExternalActionPreparation Create(PullRequestPayload payload)
    {
        var body = WriteArguments(payload);
        body["body"] = PullRequestNarrative.Body(payload);
        body["landedStatement"] = PublicationStatement;
        body["schemaVersion"] = SchemaVersion;
        body["testCommandId"] = null;
        body["testStatement"] = RemediationProposalPayloadFactory.TestStatement;
        body["title"] = PullRequestNarrative.Title(payload.OriginReportId);
        return new ExternalActionPreparation(
            RemediationPayloadFields.Utf8Bytes(CanonicalJsonSerializer.Canonicalize(body)),
            BuildReviewSummary(payload));
    }

    private static JsonObject WriteArguments(PullRequestPayload payload) => new()
    {
        ["baseBranch"] = payload.BaseBranch,
        ["baseCommitSha"] = payload.BaseCommitSha,
        ["baseTreeIdentity"] = payload.BaseTreeIdentity,
        ["correspondenceDigest"] = payload.CorrespondenceDigest,
        ["excludedPathCount"] = payload.ExcludedPathCount,
        ["filesChanged"] = payload.FilesChanged,
        ["headBranch"] = payload.HeadBranch,
        ["headCommitSha"] = payload.HeadCommitSha,
        ["issueNumber"] = payload.IssueNumber,
        ["originReportId"] = payload.OriginReportId.ToString("N"),
        ["predecessorActionId"] = payload.PredecessorActionId.ToString("N"),
        ["predecessorResultSha256"] = payload.PredecessorResultSha256,
        ["provedPathCount"] = payload.ProvedPathCount,
        ["release"] = payload.Release,
        ["reportConfidence"] = payload.ReportConfidence,
        ["repository"] = payload.Repository,
        ["resultTreeIdentity"] = payload.ResultTreeIdentity,
        ["serviceName"] = payload.ServiceName
    };

    /// <summary>
    /// What a reviewer sees before they open anything. It leads with the two things a pull request
    /// most invites a reader to assume - that something checked the change, and that opening it is a
    /// small act - and ends with the limit of what approving authorizes. The service and the release
    /// appear here and not in the published description, because this text is read by the operator who
    /// already sees them on the action row.
    /// </summary>
    private static string BuildReviewSummary(PullRequestPayload payload)
    {
        var builder = new StringBuilder(
            "UNTESTED CHANGE: no test was executed. Approve only if you accept publishing an " +
            "unverified change to reviewers. ");
        builder.Append("Open one pull request in ").Append(payload.Repository)
            .Append(" from '").Append(payload.HeadBranch)
            .Append("' at commit ").Append(payload.HeadCommitSha[..12])
            .Append(" into '").Append(payload.BaseBranch)
            .Append("', for service '").Append(payload.ServiceName)
            .Append("' at release '").Append(payload.Release)
            .Append("', carrying ").Append(Count(payload.FilesChanged))
            .Append(" changed file section(s). The report records ").Append(payload.ReportConfidence)
            .Append(" confidence");
        if (payload.IssueNumber > 0)
        {
            builder.Append(" and cites issue ").Append(Count(payload.IssueNumber));
        }

        builder.Append(". ").Append(PublicationStatement);
        return builder.ToString();
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static PullRequestPayload? TryRead(JsonElement root, bool payloadForm)
    {
        if (root.ValueKind != JsonValueKind.Object || !HasExactProperties(root, payloadForm) ||
            !RemediationPayloadFields.TryReadReportId(root, "originReportId", out var reportId) ||
            !RemediationPayloadFields.TryReadReportId(root, "predecessorActionId", out var predecessor) ||
            !RemediationPayloadFields.TryReadName(root, "serviceName", MaximumNameCharacters, out var service) ||
            !RemediationPayloadFields.TryReadName(root, "release", MaximumNameCharacters, out var release) ||
            !RemediationPayloadFields.TryReadName(root, "repository", MaximumRepositoryCharacters, out var repository) ||
            !RemediationPayloadFields.TryReadName(root, "baseBranch", MaximumNameCharacters, out var baseBranch) ||
            !RemediationPayloadFields.TryReadName(root, "headBranch", RemediationBranchName.Characters, out var head) ||
            !RemediationBranchName.IsDerived(head, reportId) ||
            !RemediationPayloadFields.TryReadGitObjectName(root, "headCommitSha", out var headCommit) ||
            !RemediationPayloadFields.TryReadGitObjectName(root, "baseCommitSha", out var baseCommit) ||
            !RemediationPayloadFields.TryReadIdentity(root, "baseTreeIdentity", out var baseIdentity) ||
            !RemediationPayloadFields.TryReadIdentity(root, "resultTreeIdentity", out var resultIdentity) ||
            !RemediationPayloadFields.TryReadIdentity(root, "correspondenceDigest", out var digest) ||
            !RemediationPayloadFields.TryReadIdentity(root, "predecessorResultSha256", out var predecessorResult) ||
            !RemediationPayloadFields.TryReadCount(root, "filesChanged", 1, MaximumFileSections, out var filesChanged) ||
            !RemediationPayloadFields.TryReadCount(root, "provedPathCount", 1, MaximumPathCount, out var proved) ||
            !RemediationPayloadFields.TryReadCount(root, "excludedPathCount", 0, MaximumPathCount, out var excluded) ||
            !RemediationPayloadFields.TryReadCount(root, "issueNumber", 0, MaximumIssueNumber, out var issue) ||
            !TryReadConfidence(root, out var confidence))
        {
            return null;
        }

        var payload = new PullRequestPayload(
            reportId, service, release, repository, baseBranch, head, headCommit, baseCommit,
            baseIdentity, resultIdentity, filesChanged, proved, excluded, digest, confidence, issue,
            predecessor, predecessorResult);
        return HasBackendStatements(root, payloadForm, payload) ? payload : null;
    }

    private static bool HasExactProperties(JsonElement root, bool payloadForm)
    {
        var expected = payloadForm ? PayloadPropertyCount : ArgumentPropertyCount;
        return root.EnumerateObject().Count() == expected &&
            root.EnumerateObject().All(property => IsArgumentProperty(property.Name) ||
                (payloadForm && property.Name is
                    "body" or "landedStatement" or "schemaVersion" or "testCommandId" or
                    "testStatement" or "title"));
    }

    private static bool IsArgumentProperty(string name) => name is
        "baseBranch" or "baseCommitSha" or "baseTreeIdentity" or "correspondenceDigest" or
        "excludedPathCount" or "filesChanged" or "headBranch" or "headCommitSha" or "issueNumber" or
        "originReportId" or "predecessorActionId" or "predecessorResultSha256" or "provedPathCount" or
        "release" or "reportConfidence" or "repository" or "resultTreeIdentity" or "serviceName";

    private static bool TryReadConfidence(JsonElement root, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty("reportConfidence", out var property) ||
            property.ValueKind != JsonValueKind.String ||
            !PullRequestNarrative.IsKnownConfidence(property.GetString()))
        {
            return false;
        }

        value = property.GetString()!;
        return true;
    }

    /// <summary>
    /// The statements and the published text a caller never supplies.
    /// </summary>
    /// <remarks>
    /// The title and the body are re-derived from the fields that were just read and compared byte for
    /// byte. A payload whose description is anything other than what this product composes from those
    /// numbers cannot have been written by this product, which is what stops an edited row from putting
    /// a sentence of someone else's choosing on a public page. The bounds are checked here rather than
    /// only at composition, so a payload that arrived from durable state is measured too.
    /// </remarks>
    private static bool HasBackendStatements(
        JsonElement root,
        bool payloadForm,
        PullRequestPayload payload)
    {
        if (!payloadForm)
        {
            return true;
        }

        var title = PullRequestNarrative.Title(payload.OriginReportId);
        var body = PullRequestNarrative.Body(payload);
        return root.GetProperty("testCommandId").ValueKind == JsonValueKind.Null &&
            root.GetProperty("schemaVersion").TryGetInt32(out var version) && version == SchemaVersion &&
            IsExactly(root, "testStatement", RemediationProposalPayloadFactory.TestStatement) &&
            IsExactly(root, "landedStatement", PublicationStatement) &&
            IsExactly(root, "title", title) && IsExactly(root, "body", body) &&
            IsWithinBounds(title, body);
    }

    private static bool IsWithinBounds(string title, string body) =>
        RemediationPayloadFields.Utf8ByteCount(title) is > 0 and <= PullRequestNarrative.MaximumTitleBytes &&
        RemediationPayloadFields.Utf8ByteCount(body) is > 0 and <= PullRequestNarrative.MaximumBodyBytes;

    private static bool IsExactly(JsonElement root, string name, string expected) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
        string.Equals(property.GetString(), expected, StringComparison.Ordinal);
}
