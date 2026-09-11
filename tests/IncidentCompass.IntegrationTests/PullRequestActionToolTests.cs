using System.Text;
using System.Text.Json;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Infrastructure.Remediation;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// What an approved pull request does with a frozen payload and one provider.
/// </summary>
public sealed class PullRequestActionToolTests
{
    private static readonly Guid ReportId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string Repository = "acme/checkout";
    private const string BaseBranch = "main";
    private const string HeadCommit = "4444444444444444444444444444444444444444";
    private const string BaseCommit = "1111111111111111111111111111111111111111";
    private const string Digest = "3333333333333333333333333333333333333333333333333333333333333333";

    [Fact]
    public async Task OneApprovedActionOpensExactlyOnePullRequestAndRecordsItsNumber()
    {
        var gateway = new StubGateway();
        var tool = new PullRequestActionTool(gateway);

        var result = await ExecuteAsync(tool);

        Assert.True(result.Succeeded);
        Assert.Equal(
            ExternalActionAuditProjection.GitHubPullRequestKind,
            result.AuditProjection!.ResourceKind);
        Assert.Equal("17", result.AuditProjection.ResourceId);
        Assert.Equal("absent", result.AuditProjection.BeforeState);
        Assert.Equal("open", result.AuditProjection.AfterState);
        using var payload = JsonDocument.Parse(result.CanonicalResult);
        Assert.False(payload.RootElement.GetProperty("merged").GetBoolean());
        Assert.Equal("17", payload.RootElement.GetProperty("pullRequestNumber").GetString());
        Assert.Equal("not_executed", payload.RootElement.GetProperty("testOutcome").GetString());
        Assert.Contains("Nothing was merged", result.Summary, StringComparison.Ordinal);
        Assert.Single(gateway.Created);
    }

    /// <summary>
    /// Replay: the adapter asks the gateway once more, the gateway's own preflight answers with the
    /// pull request that already exists, and the terminal record is the same number.
    /// </summary>
    [Fact]
    public async Task AReplayAnswersWithTheSameNumberAndTheSameProjection()
    {
        var gateway = new StubGateway { AlreadyOpenAfterFirst = true };
        var tool = new PullRequestActionTool(gateway);

        var first = await ExecuteAsync(tool);
        var second = await ExecuteAsync(tool);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal("17", first.AuditProjection!.ResourceId);
        Assert.Equal("17", second.AuditProjection!.ResourceId);
        Assert.Contains(
            "\"outcome\":\"" + CodePublicationCodes.PullRequestOpened + "\"",
            Encoding.UTF8.GetString(first.CanonicalResult),
            StringComparison.Ordinal);
        Assert.Contains(
            "\"outcome\":\"" + CodePublicationCodes.PullRequestAlreadyOpen + "\"",
            Encoding.UTF8.GetString(second.CanonicalResult),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The request the gateway receives carries the head the approval named and nothing a caller chose:
    /// no base, because the record has no field for one, and no free text.
    /// </summary>
    [Fact]
    public async Task TheRequestTheProviderSeesCarriesTheApprovedHeadAndBackendText()
    {
        var gateway = new StubGateway();
        var tool = new PullRequestActionTool(gateway);

        await ExecuteAsync(tool);

        var created = Assert.Single(gateway.Created);
        Assert.Equal(RemediationBranchName.For(ReportId), created.HeadBranch);
        Assert.Equal(HeadCommit, created.HeadCommitSha);
        Assert.Contains(ReportId.ToString("N"), created.Title, StringComparison.Ordinal);
        Assert.Contains("No test was executed", created.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// An incident, a source file or a ticket body reaches this path only through the service name and
    /// the release, which are payload fields that decide nothing. They cannot retarget anything, and
    /// they do not reach the published text at all.
    /// </summary>
    [Fact]
    public async Task InjectedTextInThePayloadCannotRetargetAnythingOrReachThePage()
    {
        const string hostile =
            "../../evil/repo main --force <!-- auto-merge-sentinel --> https://attacker.example/token=abc";
        var gateway = new StubGateway();
        var tool = new PullRequestActionTool(gateway);

        var result = await ExecuteAsync(
            tool, Payload() with { ServiceName = hostile, Release = hostile });

        Assert.True(result.Succeeded);
        var created = Assert.Single(gateway.Created);
        Assert.Equal(RemediationBranchName.For(ReportId), created.HeadBranch);
        Assert.Equal(Repository, gateway.ConfiguredRepository);
        Assert.Equal(BaseBranch, gateway.BaseBranch);
        Assert.DoesNotContain("evil/repo", created.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("attacker.example", created.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("--force", created.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("auto-merge-sentinel", created.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("evil/repo", created.Title, StringComparison.Ordinal);
    }

    /// <summary>
    /// A payload naming another repository or another base branch is refused before any provider call.
    /// The dispatcher's binding fingerprint check would already have failed it; this is the adapter
    /// saying so in its own words rather than relying on that.
    /// </summary>
    [Theory]
    [InlineData("evil/repo", BaseBranch)]
    [InlineData(Repository, "release")]
    public async Task APayloadBoundToAnotherTargetIsRefusedBeforeAnythingIsRead(
        string repository,
        string baseBranch)
    {
        var gateway = new StubGateway();
        var tool = new PullRequestActionTool(gateway);

        var result = await ExecuteAsync(
            tool, Payload() with { Repository = repository, BaseBranch = baseBranch });

        Assert.False(result.Succeeded);
        Assert.Equal(PullRequestCodes.BindingChanged, result.FailureCode);
        Assert.Empty(gateway.Created);
    }

    /// <summary>
    /// The published text is frozen in the approval and re-derived at dispatch. A payload whose body or
    /// title was edited after approval is not readable at all, so nobody else's sentence reaches a
    /// public page.
    /// </summary>
    [Theory]
    [InlineData("body")]
    [InlineData("title")]
    public async Task AnEditedTitleOrBodyIsNotExecutableAtAll(string property)
    {
        var gateway = new StubGateway();
        var tool = new PullRequestActionTool(gateway);
        var tampered = Tamper(PullRequestPayloadFactory.Create(Payload()).CanonicalPayload, property);

        var result = await tool.ExecuteAsync(
            Guid.NewGuid(), tampered, TestContext.Current.CancellationToken);

        Assert.Equal(PullRequestCodes.PayloadInvalid, result.FailureCode);
        Assert.Empty(gateway.Created);
    }

    [Fact]
    public async Task AnUnreadablePayloadIsRefusedWithoutTouchingAnything()
    {
        var gateway = new StubGateway();
        var tool = new PullRequestActionTool(gateway);

        var result = await tool.ExecuteAsync(
            Guid.NewGuid(),
            Encoding.UTF8.GetBytes("{\"headBranch\":\"main\"}"),
            TestContext.Current.CancellationToken);

        Assert.Equal(PullRequestCodes.PayloadInvalid, result.FailureCode);
        Assert.Empty(gateway.Created);
    }

    /// <summary>
    /// An unknown outcome is reported as such and is never turned into a success; the number stays
    /// absent, so nothing downstream can link to a pull request this dispatch did not confirm.
    /// </summary>
    [Fact]
    public async Task AnUnknownOutcomeIsDurableAndRecordsNoNumber()
    {
        var gateway = new StubGateway { Outcome = CodePublicationCodes.OutcomeUnknown, Number = null };
        var tool = new PullRequestActionTool(gateway);

        var result = await ExecuteAsync(tool);

        Assert.False(result.Succeeded);
        Assert.Equal(CodePublicationCodes.OutcomeUnknown, result.FailureCode);
        Assert.Null(result.AuditProjection);
    }

    private static byte[] Tamper(byte[] canonicalPayload, string property)
    {
        var text = Encoding.UTF8.GetString(canonicalPayload);
        using var document = JsonDocument.Parse(text);
        var original = document.RootElement.GetProperty(property).GetString()!;
        return Encoding.UTF8.GetBytes(text.Replace(
            JsonSerializer.Serialize(original),
            JsonSerializer.Serialize(original + " Merged by the maintainers."),
            StringComparison.Ordinal));
    }

    private static Task<ExternalActionExecutionResult> ExecuteAsync(
        PullRequestActionTool tool,
        PullRequestPayload? payload = null) =>
        tool.ExecuteAsync(
            Guid.NewGuid(),
            PullRequestPayloadFactory.Create(payload ?? Payload()).CanonicalPayload,
            TestContext.Current.CancellationToken);

    private static PullRequestPayload Payload() => new(
        ReportId,
        "checkout-service",
        "2026.9.1",
        Repository,
        BaseBranch,
        RemediationBranchName.For(ReportId),
        HeadCommit,
        BaseCommit,
        new string('1', 64),
        new string('2', 64),
        1,
        7,
        1,
        Digest,
        "Medium",
        42,
        Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa"),
        new string('4', 64));

    private sealed class StubGateway : ICodePublicationGateway
    {
        public List<CodePublicationPullRequestRequest> Created { get; } = [];

        public string Outcome { get; init; } = CodePublicationCodes.PullRequestOpened;

        /// <summary>
        /// Models what the real gateway does on a second dispatch: its unconditional listing read finds
        /// the pull request the first one opened, so it answers with that instead of creating another.
        /// </summary>
        public bool AlreadyOpenAfterFirst { get; init; }

        public int? Number { get; init; } = 17;

        public bool IsConfigured => true;

        public string? ConfiguredRepository => Repository;

        public string BaseBranch => PullRequestActionToolTests.BaseBranch;

        public string BindingFingerprint => new('a', 64);

        // A pull request never reads a base, reads a branch or pushes. Throwing rather than answering
        // means a path that started to would fail this whole file instead of passing quietly.
        public Task<CodePublicationBaseResult> ReadBaseAsync(
            string? pinnedCommitSha,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CodePublicationRefResult> ReadBranchAsync(
            string branchName,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CodePublicationRefResult> PushAsync(
            CodePublicationPushRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CodePublicationPullRequestResult> CreatePullRequestAsync(
            CodePublicationPullRequestRequest request,
            CancellationToken cancellationToken)
        {
            Created.Add(request);
            return Task.FromResult(new CodePublicationPullRequestResult(
                AlreadyOpenAfterFirst && Created.Count > 1
                    ? CodePublicationCodes.PullRequestAlreadyOpen
                    : Outcome,
                Number));
        }
    }
}
