using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;
using IncidentCompass.Infrastructure.Remediation;
using IncidentCompass.Infrastructure.SourceContext;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

/// <summary>
/// One remediation pass end to end, over a real monitored checkout with a real bug in it.
/// </summary>
/// <remarks>
/// The workspace under test is the real local adapter over a real directory rather than a stub, so
/// these exercise the same admission rules, the same tree identity and the same all-or-nothing apply
/// a worker would get. Only the model is scripted, because the model is the one thing that cannot be
/// made deterministic. Expected identities are computed by materializing the tree the fix claims to
/// produce and comparing digests, so an assertion cannot pass because the runner and the applier
/// agree with each other about a tree neither produced.
/// </remarks>
public sealed class RemediationDiffRunnerTests : IDisposable
{
    private const string ServiceName = "checkout-api";
    private const string Release = "1.4";
    private const string RouteId = "report-chat";

    /// <summary>A marker that must never reach the record: it is in a file the fix does not touch.</summary>
    private const string UntouchedSourceMarker = "source-body-must-not-reach-the-record";

    /// <summary>A marker that must never reach the record: it is in the system instructions.</summary>
    private const string InstructionsMarker = "prompt-must-not-reach-the-record";

    private const string BuggyCheckout =
        "namespace Shop;\npublic static class Checkout { public static decimal Total(decimal p, int q) => p * q - 1; }\n";

    private const string FixedCheckout =
        "namespace Shop;\npublic static class Checkout { public static decimal Total(decimal p, int q) => p * q; }\n";

    private static readonly string Untouched =
        "namespace Shop;\n// " + UntouchedSourceMarker + "\npublic static class Basket;\n";

    private static readonly string FixPatch = string.Join('\n',
        "--- a/src/Checkout.cs",
        "+++ b/src/Checkout.cs",
        "@@ -1,2 +1,2 @@",
        " namespace Shop;",
        "-public static class Checkout { public static decimal Total(decimal p, int q) => p * q - 1; }",
        "+public static class Checkout { public static decimal Total(decimal p, int q) => p * q; }",
        string.Empty);

    private readonly string temporaryRoot = Directory.CreateTempSubdirectory("ic-remediation-").FullName;

    private string MonitoredRoot => Path.Combine(temporaryRoot, "monitored");

    private string ExpectedRoot => Path.Combine(temporaryRoot, "expected");

    private string WorkspaceRoot => Path.Combine(temporaryRoot, "workspaces");

    [Fact]
    public async Task Run_RecordsTheBaseItAppliedToAndTheTreeItProduced()
    {
        WriteMonitoredCheckout();
        Write(ExpectedRoot, "src/Checkout.cs", FixedCheckout);
        Write(ExpectedRoot, "src/Basket.cs", Untouched);

        var diffs = new RecordingDiffRepository();
        var result = await RunAsync(diffs, Fenced(FixPatch));

        Assert.Equal(RemediationCodes.Produced, result.Code);
        var diff = Assert.Single(diffs.Added);
        Assert.Equal(await IdentifyAsync(MonitoredRoot), diff.BaseTreeIdentity);
        Assert.Equal(await IdentifyAsync(ExpectedRoot), diff.ResultTreeIdentity);
        Assert.NotEqual(diff.BaseTreeIdentity, diff.ResultTreeIdentity);
        Assert.Equal(1, diff.FilesChanged);
        Assert.Equal(RemediationCodes.Applied, diff.ValidationCode);
    }

    /// <summary>
    /// The record says no test ran, and there is no way for it to say anything else here.
    /// </summary>
    [Fact]
    public async Task Run_RecordsThatNoTestRan()
    {
        WriteMonitoredCheckout();
        var diffs = new RecordingDiffRepository();

        await RunAsync(diffs, Fenced(FixPatch));

        var diff = Assert.Single(diffs.Added);
        Assert.Equal("not_executed", diff.TestOutcome);
        Assert.Null(diff.TestCommandId);
    }

    /// <summary>
    /// The monitored checkout is read and never written, whatever the pass does to its own copy.
    /// </summary>
    [Fact]
    public async Task Run_LeavesTheMonitoredCheckoutExactlyAsItWas()
    {
        WriteMonitoredCheckout();
        var before = await IdentifyAsync(MonitoredRoot);

        await RunAsync(new RecordingDiffRepository(), Fenced(FixPatch));

        Assert.Equal(before, await IdentifyAsync(MonitoredRoot));
        Assert.Equal(BuggyCheckout, File.ReadAllText(Path.Combine(MonitoredRoot, "src", "Checkout.cs")));
    }

    [Fact]
    public async Task Run_RefusesAnAnswerThatIsNotAPatchAndRecordsNothing()
    {
        WriteMonitoredCheckout();
        var diffs = new RecordingDiffRepository();
        var model = ScriptOf("I could not determine a safe fix for this incident.");

        var result = await RunAsync(diffs, model);

        Assert.Equal(RemediationCodes.AnswerNotAPatch, result.Code);
        Assert.Null(result.Diff);
        Assert.Empty(diffs.Added);
    }

    /// <summary>
    /// A refusal the model could fix is worth exactly the configured reprompt allowance, and the
    /// corrections are durably visible without carrying the answer that caused them.
    /// </summary>
    [Fact]
    public async Task Run_RepromptsUpToTheConfiguredAllowanceAndThenGivesUp()
    {
        WriteMonitoredCheckout();
        var model = ScriptOf("Not a diff.", "Still not a diff.", "Nor is this.", "Nor this.");
        var writer = new RecordingLedgerWriter();

        var result = await RunAsync(new RecordingDiffRepository(), model, writer: writer);

        Assert.Equal(RemediationCodes.AnswerNotAPatch, result.Code);
        Assert.Equal(3, model.CallCount);
        var reprompts = writer.Requests
            .Where(request => request.EventType == TriageLedgerEventType.BudgetEvent)
            .Where(request => request.Rationale?.StartsWith(
                RemediationDiffRunner.RepromptRationalePrefix, StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Equal(2, reprompts.Length);
        Assert.All(reprompts, request => Assert.Equal(RemediationDiffRunner.LedgerRole, request.Role));
        Assert.All(reprompts, request => Assert.DoesNotContain("Not a diff", request.Rationale!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_AcceptsACorrectedAnswerAfterARefusal()
    {
        WriteMonitoredCheckout();
        var diffs = new RecordingDiffRepository();
        // A diff whose context line does not match the base, then the one that does.
        var model = ScriptOf(
            Fenced(string.Join('\n',
                "--- a/src/Checkout.cs",
                "+++ b/src/Checkout.cs",
                "@@ -1,2 +1,2 @@",
                " namespace Warehouse;",
                "-public static class Checkout { }",
                "+public static class Checkout { public static int Total() => 0; }",
                string.Empty)),
            Fenced(FixPatch));

        var result = await RunAsync(diffs, model);

        Assert.Equal(RemediationCodes.Produced, result.Code);
        Assert.Equal(2, model.CallCount);
        Assert.Single(diffs.Added);
    }

    /// <summary>
    /// A record carries the change and nothing around it.
    /// </summary>
    /// <remarks>
    /// Checked over every string the record holds rather than over the diff alone, because the field
    /// a leak would arrive in is the one nobody thought about. The two markers are planted where a
    /// leak would come from: one in a source file the fix does not touch, one in the operator's
    /// system instructions.
    /// </remarks>
    [Fact]
    public async Task Run_RecordsNoSourceBodyNoPromptAndNothingUnbounded()
    {
        WriteMonitoredCheckout();
        var diffs = new RecordingDiffRepository();

        await RunAsync(diffs, Fenced(FixPatch));

        var diff = Assert.Single(diffs.Added);
        var fields = new[]
        {
            diff.TenantId, diff.ServiceName, diff.Release, diff.BaseTreeIdentity, diff.ResultTreeIdentity,
            diff.PatchText, diff.RouteId, diff.Model, diff.ValidationCode, diff.TestOutcome
        };
        Assert.All(fields, field => Assert.DoesNotContain(UntouchedSourceMarker, field, StringComparison.Ordinal));
        Assert.All(fields, field => Assert.DoesNotContain(InstructionsMarker, field, StringComparison.Ordinal));
        Assert.All(fields, field => Assert.DoesNotContain(
            TriageInvestigationPromptBuilder.UntrustedContextStartMarker, field, StringComparison.Ordinal));

        Assert.Equal(FixPatch, diff.PatchText);
        Assert.Equal(Encoding.UTF8.GetByteCount(diff.PatchText), diff.PatchBytes);
        Assert.True(diff.PatchBytes <= SourcePatchLimits.RawBudgetBytes);
        Assert.Equal(64, diff.BaseTreeIdentity.Length);
        Assert.Equal(64, diff.ResultTreeIdentity.Length);
        Assert.All(fields, field => Assert.True(field.Length <= SourcePatchLimits.RawBudgetBytes));
    }

    [Fact]
    public async Task Run_RefusesWhenTheHostConfiguredNoWorkspaceRoot()
    {
        WriteMonitoredCheckout();
        var diffs = new RecordingDiffRepository();

        var result = await RunAsync(diffs, Fenced(FixPatch), workspaceConfigured: false);

        Assert.Equal(RemediationCodes.NotConfigured, result.Code);
        Assert.Empty(diffs.Added);
    }

    /// <summary>
    /// A report that cites no source is refused before a model is asked anything.
    /// </summary>
    [Fact]
    public async Task Run_RefusesWhenTheReportCitesNoSourceEvidence()
    {
        WriteMonitoredCheckout();
        var model = ScriptOf(Fenced(FixPatch));

        var result = await RunAsync(new RecordingDiffRepository(), model, sourceEvidence: []);

        Assert.Equal(RemediationCodes.SourceEvidenceMissing, result.Code);
        Assert.Equal(0, model.CallCount);
    }

    [Fact]
    public async Task Run_RefusesWhenTheConfigurationNamesNoCurrentReleaseForTheService()
    {
        WriteMonitoredCheckout();
        var model = ScriptOf(Fenced(FixPatch));

        var result = await RunAsync(new RecordingDiffRepository(), model, currentRelease: null);

        Assert.Equal(RemediationCodes.ReleaseUnavailable, result.Code);
        Assert.Equal(0, model.CallCount);
    }

    /// <summary>
    /// The remediation call rides the investigation rails, so it is charged and accounted like every
    /// other model call and is separable from investigation spend by its kind.
    /// </summary>
    [Fact]
    public async Task Run_AccountsTheModelCallOnTheLedgerUnderItsOwnKind()
    {
        WriteMonitoredCheckout();
        var writer = new RecordingLedgerWriter();

        await RunAsync(new RecordingDiffRepository(), Fenced(FixPatch), writer: writer);

        var modelCall = Assert.Single(
            writer.Requests,
            request => request.EventType == TriageLedgerEventType.ModelCall);
        using var metadata = JsonDocument.Parse(modelCall.Rationale!);
        Assert.Equal(
            TriageModelCallKinds.Remediation,
            metadata.RootElement.GetProperty("kind").GetString());
        Assert.Equal(RemediationDiffRunner.LedgerRole, modelCall.Role);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void WriteMonitoredCheckout()
    {
        Write(MonitoredRoot, "src/Checkout.cs", BuggyCheckout);
        Write(MonitoredRoot, "src/Basket.cs", Untouched);
    }

    private Task<RemediationDiffRunResult> RunAsync(
        RecordingDiffRepository diffs,
        string answer,
        bool workspaceConfigured = true,
        RecordingLedgerWriter? writer = null) =>
        RunAsync(diffs, ScriptOf(answer), workspaceConfigured, writer);

    private async Task<RemediationDiffRunResult> RunAsync(
        RecordingDiffRepository diffs,
        ScriptedModelClient model,
        bool workspaceConfigured = true,
        RecordingLedgerWriter? writer = null,
        IReadOnlyList<TriageArtifact>? sourceEvidence = null,
        string? currentRelease = Release)
    {
        var now = DateTimeOffset.UtcNow;
        var ledgerWriter = writer ?? new RecordingLedgerWriter();
        var runner = new RemediationDiffRunner(
            new InvestigationModelCaller(
                model,
                new StaticLedgerReader(),
                new TriageLedgerAppender(ledgerWriter),
                TimeProvider.System),
            CreateWorkspace(workspaceConfigured ? WorkspaceRoot : null),
            diffs,
            new TriageLedgerAppender(ledgerWriter),
            TimeProvider.System);

        var job = CreateJob(now);
        return await runner.RunAsync(
            new RemediationRequest(
                job,
                CreateFault(job.FaultId),
                CreateConfiguration(currentRelease),
                RouteId,
                "You are the IncidentCompass remediation role. " + InstructionsMarker,
                Guid.NewGuid(),
                CreateReport(),
                sourceEvidence ?? [CreateSourceEvidence(job.Id)],
                now),
            TestContext.Current.CancellationToken);
    }

    private LocalSourceRemediationWorkspace CreateWorkspace(string? workspaceRoot) =>
        new(Options.Create(new SourceContextOptions
        {
            WorkspaceRoot = workspaceRoot,
            Roots =
            [
                new SourceContextRootOptions
                {
                    ServiceName = ServiceName,
                    Release = Release,
                    RootPath = MonitoredRoot
                }
            ]
        }));

    private async Task<string> IdentifyAsync(string root)
    {
        var result = await new SourceWorkspaceMaterializer(
                Path.Combine(temporaryRoot, "identify"),
                SourceWorkspaceBounds.Default)
            .MaterializeAsync(root, TestContext.Current.CancellationToken);
        using var workspace = result.Workspace;
        Assert.NotNull(workspace);
        return workspace.TreeIdentity;
    }

    private static ScriptedModelClient ScriptOf(params string[] answers) => new(answers);

    private static string Fenced(string patch) => "```diff\n" + patch + "```\n";

    private static void Write(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static TriageArtifact CreateSourceEvidence(Guid jobId) => new(
        Guid.NewGuid(),
        jobId,
        Attempt: 1,
        ArtifactKind.RetrievedItem,
        "source:" + Release + ":src/Checkout.cs",
        CanonicalJsonSerializer.ToElement(new JsonObject
        {
            ["evidenceKind"] = "SourceCode",
            ["relativePath"] = "src/Checkout.cs",
            ["lineStart"] = 1,
            ["lineEnd"] = 2,
            ["excerpt"] = BuggyCheckout,
            ["release"] = Release,
            ["mappingMethod"] = "heuristic"
        }),
        "content-hash",
        DateTimeOffset.UtcNow);

    private static TriageReport CreateReport() => new(
        TriageReportStatus.Completed,
        "Totals are one unit short for every basket.",
        "bug",
        "high",
        [],
        ["The fix was not executed against any test."],
        "Correct the quantity multiplication in Checkout.Total.");

    private static Fault CreateFault(Guid faultId) => new(
        faultId,
        Guid.NewGuid(),
        "local",
        FaultStatus.Analyzing,
        "fingerprint",
        FingerprintVersion: 1,
        FingerprintStrength.Strong,
        CanGroup: true,
        ServiceName,
        "production",
        Severity: "error",
        CorrelationId: null,
        DateTimeOffset.UtcNow,
        CompletedAtUtc: null,
        RecurrenceOf: null);

    private static TriageJob CreateJob(DateTimeOffset now) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        TriageJobStatus.Processing,
        Attempt: 1,
        LockedBy: "worker-test",
        LockedUntilUtc: now.AddMinutes(5),
        NextAttemptAtUtc: null,
        LastErrorCode: null,
        LastErrorMessage: null,
        ConfigHash: "config-hash",
        CreatedAtUtc: now,
        UpdatedAtUtc: now);

    private static TriageConfiguration CreateConfiguration(string? currentRelease) => new(
        "config-hash",
        new Dictionary<string, TriageProviderSettings>
        {
            ["mock"] = new("Mock", Endpoint: null, ApiKeySecretRef: null)
        },
        new Dictionary<string, TriageRouteSettings>
        {
            [RouteId] = new("Chat", "mock", "test-model", Temperature: 0, MaxOutputTokens: 4000, ContextWindowTokens: 200000)
        },
        new OrchestratorSettings(
            "Investigate and publish a report.",
            RouteId,
            ["delegate", "publish_report"],
            new OrchestratorBudgetSettings(MaxWorkers: 2, MaxTokens: 100000, MaxWallClockSeconds: 600, MaxReprompts: 2)),
        new Dictionary<string, TriageRoleSettings>(),
        new Dictionary<string, TriageToolSettings>(),
        [],
        new IngestionSettings("local", ["tester"]),
        new FaultGroupingSettings(15, 30, 1, new MassIssueSettings(5, "strong")),
        RedactionSettings.Default)
    {
        CurrentReleases = currentRelease is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal) { [ServiceName] = currentRelease }
    };

    private sealed class ScriptedModelClient(IReadOnlyList<string> answers) : IAiModelClient
    {
        public int CallCount { get; private set; }

        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            var answer = answers[Math.Min(CallCount, answers.Count - 1)];
            CallCount++;
            return Task.FromResult(new AiModelResponse(
                answer,
                request.Model,
                "test-provider",
                new AiModelUsage(10, 10, 20),
                request.CorrelationId,
                ProposedToolCalls: []));
        }
    }

    private sealed class RecordingDiffRepository : IRemediationDiffRepository
    {
        public List<RemediationDiff> Added { get; } = [];

        public Task AddAsync(RemediationDiff diff, CancellationToken cancellationToken)
        {
            Added.Add(diff);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RemediationDiff>> FindForReportAsync(
            string tenantId,
            Guid reportId,
            int maximum,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemediationDiff>>(Added
                .Where(diff =>
                    string.Equals(diff.TenantId, tenantId, StringComparison.Ordinal) &&
                    diff.ReportId == reportId)
                .Take(maximum)
                .ToArray());
    }

    private sealed class StaticLedgerReader : ITriageLedgerReader
    {
        public Task<TriageBudgetLedgerUsage> ReadBudgetUsageAsync(TriageJob job, CancellationToken cancellationToken) =>
            Task.FromResult(new TriageBudgetLedgerUsage(TokensSpent: 0, WorkerCalls: 0));

        public Task<int> CountPolicyDecisionsAsync(
            TriageJob job, string toolName, string scope, TriageLedgerDecision decision,
            CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<bool> HasSuccessfulToolResultAsync(
            TriageJob job, string toolName, string scope, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<FaultLedgerEntry>> ReadByFaultIdAsync(
            Guid faultId, string tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FaultLedgerEntry>>([]);
    }

    private sealed class RecordingLedgerWriter : ITriageLedgerWriter
    {
        private long nextId;

        public List<TriageLedgerAppendRequest> Requests { get; } = [];

        public Task<TriageLedgerEntry> AppendAsync(
            TriageLedgerAppendRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new TriageLedgerEntry(
                ++nextId, request.FaultId, request.JobId, request.Attempt, request.EventType,
                request.Role, request.ToolName, request.Rationale, request.Decision, request.DecisionReason,
                request.PayloadRef, request.ConfigHash, DateTimeOffset.UtcNow, request.ToolStatus,
                request.TokensDelta, request.WorkersDelta));
        }

        public async Task<IReadOnlyList<TriageLedgerEntry>> AppendBatchAsync(
            IReadOnlyList<TriageLedgerAppendRequest> requests,
            CancellationToken cancellationToken)
        {
            var entries = new List<TriageLedgerEntry>(requests.Count);
            foreach (var request in requests)
            {
                entries.Add(await AppendAsync(request, cancellationToken));
            }

            return entries;
        }
    }
}
