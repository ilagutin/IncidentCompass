using System.Text;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Infrastructure.Remediation;
using IncidentCompass.Infrastructure.SourceContext;

namespace IncidentCompass.UnitTests;

/// <summary>
/// What the adapter behind an approved <c>code_write</c> action accepts, what it refuses, and what it
/// records about what it did.
/// </summary>
/// <remarks>
/// The workspace is stubbed here on purpose: what these assert is the adapter's own contract with the
/// approval machinery, not the filesystem behaviour behind the port, which
/// <c>RemediationDiffRunnerTests</c> already exercises over a real directory.
/// </remarks>
public sealed class RemediationApplyActionToolTests
{
    private const string Patch = "--- a/src/Checkout.cs\n+++ b/src/Checkout.cs\n@@ -1,1 +1,1 @@\n-old\n+new\n";
    private static readonly Guid ReportId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly string BaseIdentity = new('a', 64);
    private static readonly string ResultIdentity = new('b', 64);

    [Fact]
    public void Descriptor_IsACodeWriteExternalActionThatNoPolicyMayApproveOnItsOwn()
    {
        var tool = CreateTool(new StubWorkspace());

        Assert.Equal(ActionCategory.CodeWrite, tool.Category);
        Assert.Equal(RemediationApplyToolDescriptor.LogicalTargetId, tool.LogicalTargetId);
        Assert.Equal(RemediationApplyToolDescriptor.ToolId, tool.Definition.Name);
        Assert.True(ActionProposalValidator.IsLowerHexSha256(tool.AdapterBindingFingerprint));

        // Auto-approval is reserved for notifications, so a code write is always created requested.
        Assert.NotEqual(ActionCategory.Notification, tool.Category);
    }

    /// <summary>
    /// The adapter refuses to freeze arguments that say anything about a test other than that none
    /// ran, so a future artifact with a real outcome cannot be carried in a shape that says nothing
    /// was checked.
    /// </summary>
    [Fact]
    public void Validate_RefusesArgumentsThatClaimATestRan()
    {
        var arguments = Arguments();
        arguments["testOutcome"] = "passed";

        var result = CreateTool(new StubWorkspace()).Validate(CanonicalJsonSerializer.ToElement(arguments));

        Assert.False(result.IsValid);
        Assert.Equal("invalid_arguments", result.ErrorCode);
    }

    /// <summary>
    /// Governed arguments become the frozen payload, and the payload carries the two things the
    /// arguments never stated: that no test command ran and what that means.
    /// </summary>
    [Fact]
    public void Prepare_FreezesTheStatedFactsAndAddsTheUntestedStatement()
    {
        var prepared = CreateTool(new StubWorkspace())
            .Prepare(CanonicalJsonSerializer.ToElement(Arguments()));

        var text = Encoding.UTF8.GetString(prepared.CanonicalPayload);
        Assert.Contains(RemediationProposalPayloadFactory.TestStatement, text, StringComparison.Ordinal);
        Assert.Contains("\"testCommandId\":null", text, StringComparison.Ordinal);

        // The diff survives byte for byte through the canonical writer's escaping, which is the whole
        // point of the payload: what a person approves is these exact bytes, not a rendering of them.
        var frozen = JsonNode.Parse(text)!.AsObject();
        Assert.Equal(Patch, frozen["patch"]!.GetValue<string>());
    }

    /// <summary>
    /// A checkout that moved after the approval was given stops execution dead, with the adapter's
    /// own base-mismatch code and nothing written.
    /// </summary>
    /// <remarks>
    /// This is the second of two base checks. The first runs when the proposal is created; days can
    /// pass before a person approves, so the tree is named again here and the approved bytes are
    /// never applied to a tree the approver did not see. The approval ends in <c>failed</c>, which is
    /// terminal: there is no re-approval of the same action, only a fresh proposal.
    /// </remarks>
    [Fact]
    public async Task Execute_RefusesAndLandsNothingWhenTheCheckoutMoved()
    {
        var workspace = new StubWorkspace
        {
            Result = RemediationApplyResult.Refused(RemediationCodes.BaseMismatch, answerCorrectable: false)
        };

        var result = await CreateTool(workspace).ExecuteAsync(
            Guid.NewGuid(), Frozen(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(RemediationCodes.BaseMismatch, result.FailureCode);
        Assert.Contains("\"landed\":false", Encoding.UTF8.GetString(result.CanonicalResult), StringComparison.Ordinal);
        Assert.Equal(BaseIdentity, workspace.RequestedBaseTreeIdentity);
    }

    /// <summary>
    /// The approved bytes have to produce the approved tree. If they do not, the frozen payload did
    /// not fully decide the result, and the honest answer is a failure rather than a tree nobody
    /// approved.
    /// </summary>
    [Fact]
    public async Task Execute_RefusesWhenTheProducedTreeIsNotTheApprovedOne()
    {
        var workspace = new StubWorkspace
        {
            Result = RemediationApplyResult.Applied(new string('9', 64), 1)
        };

        var result = await CreateTool(workspace).ExecuteAsync(
            Guid.NewGuid(), Frozen(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(RemediationApplyActionTool.ResultMismatchCode, result.FailureCode);
    }

    [Fact]
    public async Task Execute_RefusesAPayloadItCannotRead()
    {
        var result = await CreateTool(new StubWorkspace()).ExecuteAsync(
            Guid.NewGuid(),
            Encoding.UTF8.GetBytes("{\"patch\":\"x\"}"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(RemediationApplyActionTool.PayloadInvalidCode, result.FailureCode);
    }

    /// <summary>
    /// A successful execution says, in the record it leaves, that nothing was landed and no test was
    /// executed. Those are the two things a reader of an <c>executed</c> action would otherwise be
    /// entitled to assume.
    /// </summary>
    [Fact]
    public async Task Execute_RecordsThatNothingWasLandedAndNoTestRan()
    {
        var result = await CreateTool(new StubWorkspace()).ExecuteAsync(
            Guid.NewGuid(), Frozen(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Null(result.FailureCode);
        var recorded = JsonNode.Parse(Encoding.UTF8.GetString(result.CanonicalResult))!.AsObject();
        Assert.False(recorded["landed"]!.GetValue<bool>());
        Assert.Equal(RemediationDiff.TestNotExecuted, recorded["testOutcome"]!.GetValue<string>());
        Assert.Equal(ResultIdentity, recorded["resultTreeIdentity"]!.GetValue<string>());
        Assert.Contains("no branch was pushed", result.Summary, StringComparison.Ordinal);
        Assert.Contains("no test was executed", result.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// Repointing a monitored root changes the binding, so an approval taken against the old wiring
    /// cannot execute against the new one. The dispatcher compares this value before the adapter is
    /// reached and fails with <c>adapter_binding_changed</c> when it differs.
    /// </summary>
    [Fact]
    public void AdapterBindingFingerprint_ChangesWhenAMonitoredRootIsRepointed()
    {
        var original = CreateTool(new StubWorkspace(), Options("/srv/checkouts/checkout-api"));
        var repointed = CreateTool(new StubWorkspace(), Options("/srv/checkouts/somewhere-else"));
        var same = CreateTool(new StubWorkspace(), Options("/srv/checkouts/checkout-api"));

        Assert.NotEqual(original.AdapterBindingFingerprint, repointed.AdapterBindingFingerprint);
        Assert.Equal(original.AdapterBindingFingerprint, same.AdapterBindingFingerprint);

        // And the digest is a digest: no configured path survives into it.
        Assert.DoesNotContain("checkout", original.AdapterBindingFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    private static SourceContextOptions Options(string rootPath) => new()
    {
        WorkspaceRoot = "/var/tmp/incidentcompass-workspaces",
        Roots = [new SourceContextRootOptions
        {
            ServiceName = "checkout-api",
            Release = "1.4",
            RootPath = rootPath
        }]
    };

    private static RemediationApplyActionTool CreateTool(
        StubWorkspace workspace,
        SourceContextOptions? options = null) =>
        new(workspace, Microsoft.Extensions.Options.Options.Create(options ?? new SourceContextOptions()));

    /// <summary>The frozen payload an approved action carries, built by the adapter itself.</summary>
    private static byte[] Frozen() =>
        CreateTool(new StubWorkspace()).Prepare(CanonicalJsonSerializer.ToElement(Arguments())).CanonicalPayload;

    private static JsonObject Arguments() => new()
    {
        ["baseTreeIdentity"] = BaseIdentity,
        ["evidenceCount"] = 1,
        ["evidenceSha256"] = new string('c', 64),
        ["filesChanged"] = 1,
        ["originReportId"] = ReportId.ToString("N"),
        ["patch"] = Patch,
        ["patchBytes"] = Encoding.UTF8.GetByteCount(Patch),
        ["release"] = "1.4",
        ["resultTreeIdentity"] = ResultIdentity,
        ["serviceName"] = "checkout-api",
        ["testOutcome"] = RemediationDiff.TestNotExecuted
    };

    private sealed class StubWorkspace : IRemediationWorkspace
    {
        public RemediationApplyResult Result { get; set; } = RemediationApplyResult.Applied(ResultIdentity, 1);

        public string? RequestedBaseTreeIdentity { get; private set; }

        public Task<RemediationBaseResult> IdentifyBaseAsync(
            RemediationTarget target,
            CancellationToken cancellationToken) =>
            Task.FromResult(RemediationBaseResult.Identified(BaseIdentity));

        public Task<RemediationApplyResult> ApplyAsync(
            RemediationApplyRequest request,
            CancellationToken cancellationToken)
        {
            RequestedBaseTreeIdentity = request.BaseTreeIdentity;
            return Task.FromResult(Result);
        }
    }
}
