using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Remediation;

namespace IncidentCompass.UnitTests;

/// <summary>
/// What a branch-push approval is taken over, and what it refuses to be taken over.
/// </summary>
public sealed class BranchPushPayloadTests
{
    private static readonly Guid ReportId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid PredecessorId = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");

    [Fact]
    public void ArgumentsRoundTripThroughTheirOwnReader()
    {
        var payload = Payload();

        var read = BranchPushPayloadFactory.TryReadArguments(
            BranchPushPayloadFactory.BuildArguments(payload));

        Assert.Equal(payload, read);
    }

    [Fact]
    public void TheFrozenPayloadCarriesTheStatementsNoCallerSupplies()
    {
        var prepared = BranchPushPayloadFactory.Create(Payload());

        using var document = JsonDocument.Parse(prepared.CanonicalPayload);
        Assert.Equal(
            BranchPushPayloadFactory.PublicationStatement,
            document.RootElement.GetProperty("landedStatement").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("testCommandId").ValueKind);
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.NotNull(BranchPushPayloadFactory.TryReadPayload(prepared.CanonicalPayload));

        // The statement now says what executing a push sets in motion, because it does set something
        // in motion. A reviewer reading only this sentence should learn that a pull-request proposal
        // follows and that it is a decision of its own.
        Assert.Contains(
            "pull-request proposal",
            BranchPushPayloadFactory.PublicationStatement,
            StringComparison.Ordinal);
        Assert.Contains(
            "no pull request is opened",
            BranchPushPayloadFactory.PublicationStatement,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The statements are inside the hashed bytes, so a payload that quietly says something else
    /// about what a push does is not readable at all rather than readable and wrong.
    /// </summary>
    [Fact]
    public void APayloadWhoseStatementWasRewrittenIsRefused()
    {
        var body = Body(Payload());
        body["landedStatement"] = "Approving merges the change into the default branch.";

        Assert.Null(BranchPushPayloadFactory.TryReadPayload(Canonical(body)));
    }

    [Fact]
    public void AnExtraPropertyIsRefused()
    {
        var body = Body(Payload());
        body["force"] = true;

        Assert.Null(BranchPushPayloadFactory.TryReadPayload(Canonical(body)));
    }

    /// <summary>
    /// The branch is derived from the report, and the reader re-derives it rather than trusting what
    /// the bytes say. A payload naming any other branch - a protected one, another incident's, one
    /// carrying an extra path segment - is unreadable.
    /// </summary>
    [Theory]
    [InlineData("main")]
    [InlineData("incidentcompass/remediation/00000000000000000000000000000000")]
    [InlineData("incidentcompass/remediation/11111111222233334444555555555555/../../main")]
    public void ABranchNameThatIsNotDerivedFromTheReportIsRefused(string branchName)
    {
        var body = Body(Payload() with { BranchName = branchName });

        Assert.Null(BranchPushPayloadFactory.TryReadPayload(Canonical(body)));
        Assert.Null(BranchPushPayloadFactory.TryReadArguments(Arguments(body)));
    }

    [Theory]
    [InlineData("commitTimestamp", "2026-09-11T12:00:00.000Z")]
    [InlineData("commitTimestamp", "2026-09-11T12:00:00+00:00")]
    [InlineData("baseCommitSha", "not-a-commit")]
    [InlineData("baseTreeSha", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("correspondenceDigest", "abc")]
    public void AFieldOutsideItsClosedShapeIsRefused(string property, string value)
    {
        var body = Body(Payload());
        body[property] = value;

        Assert.Null(BranchPushPayloadFactory.TryReadPayload(Canonical(body)));
    }

    [Fact]
    public void PatchBytesMustAgreeWithThePatchItClaimsToMeasure()
    {
        var body = Body(Payload());
        body["patchBytes"] = 9999;

        Assert.Null(BranchPushPayloadFactory.TryReadPayload(Canonical(body)));
    }

    /// <summary>
    /// Everything that decides where a push lands changes the frozen bytes, so a standing approval
    /// cannot be re-pointed at another repository, base branch or parent commit.
    /// </summary>
    [Fact]
    public void EveryTargetingFieldChangesTheFrozenBytes()
    {
        var baseline = BranchPushPayloadFactory.Create(Payload()).CanonicalPayload;

        Assert.NotEqual(baseline, BranchPushPayloadFactory.Create(
            Payload() with { Repository = "someone-else/repo" }).CanonicalPayload);
        Assert.NotEqual(baseline, BranchPushPayloadFactory.Create(
            Payload() with { BaseBranch = "release" }).CanonicalPayload);
        Assert.NotEqual(baseline, BranchPushPayloadFactory.Create(
            Payload() with { BaseCommitSha = new string('b', 40) }).CanonicalPayload);
        Assert.NotEqual(baseline, BranchPushPayloadFactory.Create(
            Payload() with { PredecessorActionId = Guid.NewGuid() }).CanonicalPayload);
        Assert.NotEqual(baseline, BranchPushPayloadFactory.Create(
            Payload() with { CorrespondenceDigest = new string('c', 64) }).CanonicalPayload);
    }

    [Fact]
    public void TheProposalKeyNamesOneReportAndOneTool() =>
        Assert.Equal(
            $"post-report:v1:{ReportId:N}:{BranchPushToolDescriptor.ToolId}",
            BranchPushPayloadFactory.ProposalKey(ReportId));

    [Fact]
    public void TheReviewSummaryLeadsWithWhatWasNotChecked()
    {
        var summary = BranchPushPayloadFactory.Create(Payload()).ReviewSummary;

        Assert.StartsWith("UNTESTED CHANGE", summary, StringComparison.Ordinal);
        Assert.Contains("No existing reference is moved or deleted", summary, StringComparison.Ordinal);
        Assert.Contains("1 local-only path(s) excluded", summary, StringComparison.Ordinal);
    }

    private static BranchPushPayload Payload() => new(
        ReportId,
        "checkout-service",
        "2026.9.1",
        "acme/checkout",
        "main",
        RemediationBranchName.For(ReportId),
        new string('a', 40),
        new string('d', 40),
        new string('1', 64),
        new string('2', 64),
        1,
        Encoding.UTF8.GetByteCount(PatchText),
        PatchText,
        new string('3', 64),
        42,
        1,
        PredecessorId,
        new string('4', 64),
        new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));

    private const string PatchText =
        "--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1,1 +1,1 @@\n-namespace A;\n+namespace B;\n";

    private static JsonObject Body(BranchPushPayload payload) =>
        JsonNode.Parse(BranchPushPayloadFactory.Create(payload).CanonicalPayload)!.AsObject();

    private static byte[] Canonical(JsonObject body) =>
        Encoding.UTF8.GetBytes(CanonicalJsonSerializer.Canonicalize(body));

    private static JsonElement Arguments(JsonObject body)
    {
        var arguments = body.DeepClone().AsObject();
        arguments.Remove("landedStatement");
        arguments.Remove("schemaVersion");
        arguments.Remove("testCommandId");
        arguments.Remove("testStatement");
        return CanonicalJsonSerializer.ToElement(arguments);
    }
}
