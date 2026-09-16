using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Governance.Validation;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Domain.Governance;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The two halves of the domain-reference boundary, tested apart because they cover different
/// things: <see cref="ArtifactDomainRef"/> bounds the shape of the value a tool may express, and
/// <see cref="RedactedToolArtifactFactory"/> runs over it the same redaction it runs over the
/// payload beside it. Neither replaces the other, so a regression in either has to fail here.
/// </summary>
public sealed class ArtifactDomainRefTests
{
    // Kept split so the source literal never matches a secret scanner; the redactor still sees one key.
    private const string AwsAccessKey = "AKIA" + "ABCDEFGHIJKLMNOP";

    private static readonly DateTimeOffset Now = new(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The character half of the test the acceptance criterion asks for: a domain reference cannot
    /// carry free text. The rule refuses two things, and this theory and
    /// <see cref="Create_RefusesAnInvisibleCharacterInASegment" /> are its two halves - the first
    /// covers characters that <em>break</em> the line, the second characters that <em>hide</em> part
    /// of it. Neither is the whole rule and neither overlaps the other: every code point below is a
    /// control character or non-space whitespace, and every code point there is a format character
    /// that is neither.
    /// <para>
    /// This half, then: a second line, a terminal escape sequence, a truncation point for anything
    /// that reads the value as a C string, or a whitespace character a renderer treats as a line
    /// ending. What survives is a single short line.
    /// </para>
    /// <para>
    /// The code points are passed as integers and the segment is assembled here rather than written
    /// as string literals, because C# treats several of them as line terminators in source and will
    /// not compile the literal at all. Building them at run time also keeps the file free of raw
    /// control bytes, which no editor, diff or review tool renders honestly.
    /// </para>
    /// <para>
    /// What the two halves together prove is bounded, and the bound is worth stating: they prove the
    /// value stays a single short line of visible text, which is what rules out an excerpt, a stack
    /// trace or a pasted multi-line secret block. They do not prove that no secret-shaped token can
    /// appear, which is what the redaction tests further down are for.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0x00)] // NUL, which truncates the name for anything that reaches a C string.
    [InlineData(0x07)] // BEL.
    [InlineData(0x09)] // Tab.
    [InlineData(0x0a)] // Line feed: the one that would let an excerpt into the column.
    [InlineData(0x0d)] // Carriage return.
    [InlineData(0x1b)] // Escape, which drives a terminal rendering an operator's query result.
    [InlineData(0x85)] // Next line, a C1 control that is also whitespace.
    [InlineData(0xa0)] // No-break space: whitespace that is not the plain space.
    [InlineData(0x2028)] // Line separator: ends a line for a renderer without being a line feed.
    [InlineData(0x2029)] // Paragraph separator.
    [InlineData(0x3000)] // Ideographic space.
    public void Create_RefusesAControlOrNonSpaceWhitespaceCharacterInASegment(int codePoint)
    {
        var segment = "src/A" + (char)codePoint + ".cs";

        Assert.Throws<ArgumentException>(() => ArtifactDomainRef.Create("source", "r1", segment));
    }

    /// <summary>
    /// The structural half: a segment that is empty or smuggles the separator, and a kind that is
    /// not the lower-case snake case the families are written in. Each would produce a value that
    /// renders as a reference and parses back as a different one.
    /// </summary>
    [Theory]
    // The separator itself, which would let one segment claim to be two.
    [InlineData("ticket", new[] { "github", "owner/repo", "42:99" })]
    // An empty segment, which renders as a reference with a hole in it that nothing can parse back.
    [InlineData("ticket", new[] { "github", "", "42" })]
    // A kind that is not lower-case snake case, in each of the ways it can fail.
    [InlineData("Source", new[] { "r1" })]
    [InlineData("source-code", new[] { "r1" })]
    [InlineData("source ref", new[] { "r1" })]
    [InlineData("1source", new[] { "r1" })]
    [InlineData("_source", new[] { "r1" })]
    [InlineData("", new[] { "r1" })]
    public void Create_RefusesAMalformedSegmentOrKind(string kind, string[] segments) =>
        Assert.Throws<ArgumentException>(() => ArtifactDomainRef.Create(kind, segments));

    [Fact]
    public void Create_RefusesASegmentPastItsLengthCap()
    {
        var overlongSegment = new string('a', ArtifactDomainRef.MaximumSegmentLength + 1);

        Assert.Throws<ArgumentException>(
            () => ArtifactDomainRef.Create("source", "r1", overlongSegment));
    }

    /// <summary>
    /// The whole-value cap is separate from the per-segment cap and has to be reachable on its own:
    /// three segments each inside their own cap still render past the reference's cap.
    /// </summary>
    [Fact]
    public void Create_RefusesAWholeReferencePastItsLengthCap()
    {
        var segment = new string('a', ArtifactDomainRef.MaximumSegmentLength);

        var refused = Assert.Throws<ArgumentException>(
            () => ArtifactDomainRef.Create("source", segment, segment, segment));

        Assert.DoesNotContain(segment, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RefusesNoSegmentsAtAll() =>
        Assert.Throws<ArgumentException>(() => ArtifactDomainRef.Create("worker"));

    /// <summary>
    /// A refusal message reaches logs, and the value it would quote is exactly the untrusted
    /// connector text this type exists to bound. The message therefore names the rule and the
    /// segment's position and nothing else.
    /// </summary>
    [Fact]
    public void Create_DoesNotEchoTheRefusedValueIntoItsMessage()
    {
        var refused = Assert.Throws<ArgumentException>(
            () => ArtifactDomainRef.Create(
                "source", "r1", "src/Secrets.cs" + (char)0x0a + AwsAccessKey));

        Assert.DoesNotContain(AwsAccessKey, refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("src/Secrets.cs", refused.Message, StringComparison.Ordinal);
        Assert.Contains("control, format", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the statement above: the characters that hide rather than break. None of
    /// these is a control character or whitespace, so a rule written on <c>char.IsControl</c> and
    /// <c>char.IsWhiteSpace</c> let every one of them through while refusing everything in the theory
    /// above - which is why a value could pass that rule, be a single line by every measure it
    /// applied, and still not say what it holds to the operator reading it.
    /// </summary>
    [Theory]
    [InlineData(0x200b)] // Zero-width space: an invisible break inside an identifier.
    [InlineData(0x200e)] // Left-to-right mark.
    [InlineData(0x202e)] // Right-to-left override: reverses the rendering of everything after it.
    [InlineData(0xfeff)] // Byte-order mark used as a zero-width no-break space.
    [InlineData(0x2060)] // Word joiner.
    [InlineData(0x2066)] // Left-to-right isolate.
    [InlineData(0x2069)] // Pop directional isolate.
    // Not a format character but the same problem: an unpaired surrogate is not well-formed UTF-16
    // and no reader can say what it renders as.
    [InlineData(0xd800)]
    public void Create_RefusesAnInvisibleCharacterInASegment(int codePoint)
    {
        var segment = "src/A" + (char)codePoint + ".cs";

        Assert.Throws<ArgumentException>(() => ArtifactDomainRef.Create("source", "r1", segment));
    }

    /// <summary>
    /// The Unicode tag block, which is the case a per-<c>char</c> rule could not have caught at all:
    /// each of these costs two UTF-16 code units, so neither half is a control character or
    /// whitespace on its own, and a segment inside the length cap can carry a hundred of them.
    /// </summary>
    [Fact]
    public void Create_RefusesAnAstralFormatCharacterInASegment()
    {
        var tagCharacter = char.ConvertFromUtf32(0xe0041);

        Assert.Throws<ArgumentException>(
            () => ArtifactDomainRef.Create("source", "r1", "src/A" + tagCharacter + ".cs"));
    }

    /// <summary>
    /// And the direction the rule must not overshoot in. An emoji is a printable symbol that a real
    /// filename can hold, and refusing it would lose a legitimate <c>source_lookup</c> hit for the
    /// same non-reason refusing a space would.
    /// </summary>
    [Fact]
    public void Create_AcceptsALegitimateAstralCodePointInASegment()
    {
        var emoji = char.ConvertFromUtf32(0x1f600);

        var reference = ArtifactDomainRef.Create("source", "r1", "docs/release " + emoji + ".md");

        Assert.Equal("source:r1:docs/release " + emoji + ".md", reference.Value);
    }

    /// <summary>
    /// A null element inside the array is a caller mistake, not a connector value, and it has to be
    /// refused rather than dereferenced: the rendered reference would otherwise carry an empty
    /// segment that nothing can parse back.
    /// </summary>
    [Fact]
    public void Create_RefusesANullSegmentElement() =>
        Assert.Throws<ArgumentException>(
            () => ArtifactDomainRef.Create("source", "r1", null!));

    /// <summary>
    /// The two forms agree that a null argument is refused and differ only in how each refuses it.
    /// Pinning them in one test keeps the pair from drifting into one form accepting what the other
    /// refuses.
    /// </summary>
    [Fact]
    public void Create_ThrowsForANullArgumentWhereTryCreateReturnsNothing()
    {
        Assert.Throws<ArgumentNullException>(() => ArtifactDomainRef.Create(null!, "r1"));
        Assert.Null(ArtifactDomainRef.TryCreate(null!, "r1"));
        Assert.Throws<ArgumentNullException>(() => ArtifactDomainRef.Create("source", (string[])null!));
        Assert.Null(ArtifactDomainRef.TryCreate("source", (string[])null!));
    }

    /// <summary>
    /// The whole-reference cap is exact, not approximate, because it is the only bound on what goes
    /// into an untyped text column. One code unit either side of it decides the outcome.
    /// </summary>
    [Fact]
    public void Create_AcceptsTheExactWholeReferenceCapAndRefusesOneCodeUnitPastIt()
    {
        var full = new string('a', ArtifactDomainRef.MaximumSegmentLength);
        // "source" plus three separators plus two full segments leaves exactly this much room.
        var remainder = ArtifactDomainRef.MaximumLength - "source".Length - 3 -
            (2 * ArtifactDomainRef.MaximumSegmentLength);

        var accepted = ArtifactDomainRef.Create("source", full, full, new string('a', remainder));

        Assert.Equal(ArtifactDomainRef.MaximumLength, accepted.Value.Length);
        Assert.Throws<ArgumentException>(
            () => ArtifactDomainRef.Create("source", full, full, new string('a', remainder + 1)));
    }

    /// <summary>
    /// The non-throwing form has to refuse everything the throwing form refuses, and refuse it by
    /// returning nothing. This is what lets a worker tool holding a path it cannot express drop one
    /// match instead of raising an exception that nothing on the worker path classifies, which would
    /// repeat identically on every retry and dead-letter the job.
    /// </summary>
    [Theory]
    // A release name carrying the separator, such as an ISO-8601 release id.
    [InlineData("source", new[] { "2026-09-12T14:03:00Z", "src/Checkout.cs" })]
    [InlineData("source", new[] { "r1", "" })]
    [InlineData("source", new[] { "r1", null })]
    [InlineData("Source", new[] { "r1" })]
    [InlineData("source", new string[0])]
    public void TryCreate_ReturnsNothingInsteadOfThrowingForARefusedShape(string kind, string[] segments) =>
        Assert.Null(ArtifactDomainRef.TryCreate(kind, segments));

    /// <summary>
    /// The case that made the non-throwing form necessary in the first place: a repository-relative
    /// path past the segment cap, which nothing bounds before it arrives and a deep monorepo produces
    /// without anyone misconfiguring anything.
    /// </summary>
    [Fact]
    public void TryCreate_ReturnsNothingForAPathPastTheSegmentCap() =>
        Assert.Null(ArtifactDomainRef.TryCreate(
            "source", "r1", new string('a', ArtifactDomainRef.MaximumSegmentLength + 1)));

    [Fact]
    public void TryCreate_ReturnsNothingForAnInvisibleCharacterInASegment() =>
        Assert.Null(ArtifactDomainRef.TryCreate("source", "r1", "src/A" + (char)0x200b + ".cs"));

    [Fact]
    public void TryCreate_ReturnsTheSameValueCreateWouldForAnAcceptedShape()
    {
        var reference = ArtifactDomainRef.TryCreate("source", "r1", "src/Checkout.cs");

        Assert.Equal("source:r1:src/Checkout.cs", reference?.Value);
    }

    /// <summary>
    /// The rule the configuration load boundary reuses, so that a release name or a role key that
    /// could never be a reference is refused while the host is starting rather than turning every
    /// later lookup that quotes it into a refusal.
    /// </summary>
    [Theory]
    [InlineData("2026.09.01", true)]
    [InlineData("release 12", true)]
    [InlineData("2026-09-12T14:03:00Z", false)]
    [InlineData("", false)]
    public void IsValidSegment_AnswersTheSameRuleTheConstructorsEnforce(string segment, bool expected) =>
        Assert.Equal(expected, ArtifactDomainRef.IsValidSegment(segment));

    /// <summary>
    /// The shipped shapes, pinned byte for byte against what the interpolated strings they replaced
    /// produced. A change here is a change to durable state that already exists:
    /// <c>ExistingTicketEvidenceShape</c> compares a stored <c>ticket:</c> reference against a string
    /// it rebuilds itself before a governed comment may be sent, and the re-triage scheduler copies
    /// references forward verbatim.
    /// </summary>
    [Theory]
    [InlineData("source:2026.09.01:src/Checkout.cs", "source", new[] { "2026.09.01", "src/Checkout.cs" })]
    [InlineData("ticket:github:owner/repo:42", "ticket", new[] { "github", "owner/repo", "42" })]
    [InlineData("worker:analysis", "worker", new[] { "analysis" })]
    [InlineData(
        "memory_item:10000000-0000-0000-0000-000000000001",
        "memory_item",
        new[] { "10000000-0000-0000-0000-000000000001" })]
    // A real checkout can hold a path with a space in it, and refusing that would turn a legitimate
    // source_lookup hit into a thrown exception in the middle of an investigation.
    [InlineData("source:r1:src/Checkout Handler.cs", "source", new[] { "r1", "src/Checkout Handler.cs" })]
    public void Create_ProducesTheShippedShapesByteForByte(string expected, string kind, string[] segments)
    {
        var reference = ArtifactDomainRef.Create(kind, segments);

        Assert.Equal(expected, reference.Value);
        Assert.Equal(expected, reference.ToString());
    }

    /// <summary>
    /// The anchor of the second half. A bounded shape rules out a document; it does not rule out a
    /// token, because a token is a short single-line run of printable characters and a repository
    /// really can hold a file whose name is one. So the reference takes the same redaction pass the
    /// payload takes, and the row says so: <c>RedactionApplied</c> answers whether the redactor
    /// removed anything from this artifact, and a reference that lost a value is something removed
    /// even when the payload beside it was untouched.
    /// </summary>
    [Fact]
    public void Create_RedactsASecretShapedDomainReferenceAndRecordsThatItDid()
    {
        var draft = new ToolArtifactDraft(
            ArtifactKind.RetrievedItem,
            ArtifactDomainRef.Create("source", "r1", "keys/" + AwsAccessKey + ".cs"),
            new JsonObject { ["evidenceKind"] = "SourceCode", ["excerpt"] = "var retries = 3;" });

        var artifact = RedactedToolArtifactFactory.Create(Job(), draft, RedactionSettings.Default, Now);

        Assert.DoesNotContain(AwsAccessKey, artifact.DomainRef!, StringComparison.Ordinal);
        Assert.Equal("source:r1:keys/[REDACTED].cs", artifact.DomainRef);
        // The payload held nothing credential-shaped, so this outcome comes from the reference alone.
        Assert.Equal("var retries = 3;", artifact.RedactedPayload.GetProperty("excerpt").GetString());
        Assert.True(artifact.RedactionApplied);
    }

    /// <summary>
    /// The other direction, and the one that keeps the guarantee useful. An ordinary reference has to
    /// survive byte for byte, because a stored <c>ticket:</c> reference is compared against a rebuilt
    /// string before a governed comment may be sent, and because a row claiming a withholding that
    /// did not happen would put a false limitation into a published report.
    /// </summary>
    [Fact]
    public void Create_LeavesAnOrdinaryDomainReferenceUntouchedAndSaysNothingWasRemoved()
    {
        var draft = new ToolArtifactDraft(
            ArtifactKind.RetrievedItem,
            ArtifactDomainRef.Create("ticket", "github", "owner/repo", "42"),
            new JsonObject { ["evidenceKind"] = "ExistingTicket", ["title"] = "Checkout times out" });

        var artifact = RedactedToolArtifactFactory.Create(Job(), draft, RedactionSettings.Default, Now);

        Assert.Equal("ticket:github:owner/repo:42", artifact.DomainRef);
        Assert.False(artifact.RedactionApplied);
    }

    /// <summary>
    /// The pairing invariant, which the reference half of this boundary is the first thing able to
    /// break. Both citable kinds a tool call produces - the per-item <c>RetrievedItem</c> artifacts
    /// and the <c>ToolResult</c> row built from the same call's output - have to record the same
    /// redaction outcome, because they carry the same connector text and ground equally well: if only
    /// one of them said so, a model would decide whether the report's limitation appeared by choosing
    /// which of the two to cite.
    /// <para>
    /// A reference-only redaction is exactly the case that splits them. The output document holds no
    /// credential, so the <c>ToolResult</c> row's own pass changes nothing, while the artifact's
    /// domain reference loses a value. The row therefore has to answer for the whole call rather than
    /// for its own document.
    /// </para>
    /// <para>
    /// It lives here rather than beside the other <see cref="WorkerToolCallExecutor"/> tests because
    /// no unit test drives that executor's commit path with a committer it can observe - the ones
    /// that touch it assert on the advertised tool surface or on fail-closed propagation - and the
    /// invariant being defended is this boundary's.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CommitSucceeded_MarksTheToolResultRedactedWhenOnlyAnArtifactReferenceWas()
    {
        var committer = new RecordingToolResultCommitter();
        var configuration = ExecutorConfiguration();
        var executor = new WorkerToolCallExecutor(
            [new SecretReferenceTool()],
            new ToolRuleEngine(new PermissiveLedgerReader()),
            new TriageLedgerAppender(new AcceptingLedgerWriter()),
            committer,
            TimeProvider.System);
        var job = Job();

        await executor.ExecuteAsync(
            job,
            configuration,
            InvestigationContext(job),
            "analysis",
            new AiToolCall("call-1", SecretReferenceTool.ToolName, "v1", EmptyObject()),
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);

        var request = Assert.Single(committer.Requests);
        var artifact = Assert.Single(request.AdditionalArtifacts!);
        Assert.DoesNotContain(AwsAccessKey, request.Output.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(AwsAccessKey, artifact.DomainRef!, StringComparison.Ordinal);
        Assert.True(artifact.RedactionApplied);
        Assert.True(request.RedactionApplied);
    }

    private static TriageConfiguration ExecutorConfiguration()
    {
        var configuration = TestTriageConfiguration.Create();
        var roles = configuration.Roles.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        roles["analysis"] = new TriageRoleSettings(
            "analysis-chat", "analysis instructions", [SecretReferenceTool.ToolName], "{}");
        var tools = configuration.Tools.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        tools[SecretReferenceTool.ToolName] = new TriageToolSettings("internal", null, null, null);
        return configuration with { Roles = roles, Tools = tools };
    }

    private static TriageJobInvestigationContext InvestigationContext(TriageJob job)
    {
        var fault = new Fault(
            job.FaultId, Guid.NewGuid(), "tenant", FaultStatus.Analyzing, "fingerprint", 1,
            FingerprintStrength.Strong, true, "checkout", "test", "Error", null, Now, null, null);
        var signal = new Signal(
            Guid.NewGuid(), "tenant", "tester", job.FaultId, "fingerprint", 1, FingerprintStrength.Strong,
            true, null, false, null, null, null, null, null, "checkout", "test", null, "Error",
            "TimeoutException", "Checkout timed out", "summary", null, null, null, null, null,
            EmptyObject(), EmptyObject(), Now, Now, null);
        return new TriageJobInvestigationContext(fault, signal, []);
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    /// <summary>
    /// A tool whose output is clean and whose artifact reference is not: a repository really can hold
    /// a file whose name is a credential, and that is the only shape in which the two halves of the
    /// pairing can disagree.
    /// </summary>
    private sealed class SecretReferenceTool : IImmediateAgentTool
    {
        public const string ToolName = "reference_probe";

        public AiToolDefinition Definition { get; } = new(ToolName, "Probe tool.", "v1", EmptyObject());

        public ToolValidationResult Validate(JsonElement arguments) => ToolValidationResult.Valid(arguments);

        public Task<ToolExecutionResult> ExecuteAsync(
            AgentToolExecutionContext context,
            JsonElement sanitizedArguments,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ToolExecutionResult(
                ToolExecutionStatus.Succeeded,
                CleanOutput(),
                Artifacts: [
                    new ToolArtifactDraft(
                        ArtifactKind.RetrievedItem,
                        ArtifactDomainRef.Create("source", "r1", "keys/" + AwsAccessKey + ".cs"),
                        new JsonObject { ["evidenceKind"] = "SourceCode", ["excerpt"] = "var retries = 3;" })
                ]));

        private static JsonElement CleanOutput()
        {
            using var document = JsonDocument.Parse("""{"matched":true,"items":[]}""");
            return document.RootElement.Clone();
        }
    }

    private sealed class RecordingToolResultCommitter : ITriageToolResultCommitter
    {
        public List<TriageToolResultCommitRequest> Requests { get; } = [];

        public Task<TriageArtifact> CommitSucceededAsync(
            TriageToolResultCommitRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new TriageArtifact(
                Guid.NewGuid(), request.Job.Id, request.Job.Attempt, ArtifactKind.ToolResult,
                request.ToolName, request.Output, request.ContentHash, Now)
            {
                RedactionApplied = request.RedactionApplied
            });
        }
    }

    private sealed class PermissiveLedgerReader : ITriageLedgerReader
    {
        public Task<TriageBudgetLedgerUsage> ReadBudgetUsageAsync(TriageJob job, CancellationToken cancellationToken) =>
            Task.FromResult(new TriageBudgetLedgerUsage(0, 0));

        public Task<int> CountPolicyDecisionsAsync(
            TriageJob job, string toolName, ToolRuleScope scope, TriageLedgerDecision decision,
            CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<bool> HasSuccessfulToolResultAsync(
            TriageJob job, string toolName, ToolRuleScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<FaultLedgerEntry>> ReadByFaultIdAsync(
            Guid faultId, string tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FaultLedgerEntry>>([]);
    }

    private sealed class AcceptingLedgerWriter : ITriageLedgerWriter
    {
        private long nextId;

        public Task<TriageLedgerEntry> AppendAsync(
            TriageLedgerAppendRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TriageLedgerEntry(
                ++nextId, request.FaultId, request.JobId, request.Attempt, request.EventType,
                request.Role, request.ToolName, request.Rationale, request.Decision, request.DecisionReason,
                request.PayloadRef, request.ConfigHash, Now, request.ToolStatus,
                request.TokensDelta, request.WorkersDelta));

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

    private static TriageJob Job() => new(
        Guid.Parse("90000000-0000-0000-0000-000000000001"),
        Guid.Parse("90000000-0000-0000-0000-000000000002"),
        TriageJobStatus.Processing,
        1,
        "worker",
        Now.AddMinutes(5),
        null,
        null,
        null,
        "config-hash-1",
        Now,
        Now);
}
