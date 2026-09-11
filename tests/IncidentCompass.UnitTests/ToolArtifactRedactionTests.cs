using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Intake.Redaction;
using IncidentCompass.Application.Memory;
using IncidentCompass.Application.SourceContext;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Domain.Governance;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.UnitTests;

public sealed class ToolArtifactRedactionTests
{
    // Kept split so the source literal never matches a secret scanner; the redactor still sees one key.
    private const string AwsAccessKey = "AKIA" + "ABCDEFGHIJKLMNOP";
    private const string ReporterEmail = "oncall.lead@example.test";

    /// <summary>
    /// Source that carries nothing credential-shaped. The whole product is grounded in this text, so
    /// the redactor has to hand it back untouched: no rule may fire on <c>Bearer header</c>,
    /// <c>risk-scored</c>, <c>sessionId</c> or <c>tokenCount</c>.
    /// </summary>
    private const string OrdinarySourceExcerpt = """
        public sealed class CheckoutHandler
        {
            private const int MaxRetries = 3;

            // Validates the Bearer header before dispatch and records risk-scored retries.
            public async Task<CheckoutResult> HandleAsync(CheckoutRequest request, CancellationToken cancellationToken)
            {
                var sessionId = request.SessionId;
                if (request.Items.Count == 0)
                {
                    return CheckoutResult.Empty(sessionId);
                }

                var response = await client.PostAsync("/checkout", request.Body, cancellationToken);
                return response.IsSuccessStatusCode
                    ? CheckoutResult.Ok(sessionId, tokenCount: response.Headers.Count)
                    : CheckoutResult.Failed(response.StatusCode);
            }
        }
        """;

    private static readonly string CredentialBearingSourceExcerpt = string.Join(
        '\n',
        "private const string LegacyAccessKey = \"" + AwsAccessKey + "\";",
        "if (request.Password == expectedHash)",
        "{",
        "    connectionString = \"Host=db;Password=hunter2;Database=checkout\";",
        "}",
        "logger.LogInformation(\"checkout retry {Attempt}\", attempt);");

    [Fact]
    public void Create_RemovesSeededSecretsAndHashesTheStoredForm()
    {
        var job = Job();
        var draft = new ToolArtifactDraft(
            ArtifactKind.RetrievedItem,
            "source:2026.09.01:src/Checkout.cs",
            SourcePayload(CredentialBearingSourceExcerpt));

        var artifact = RedactedToolArtifactFactory.Create(job, draft, Redaction(), Now);

        var stored = artifact.RedactedPayload.GetRawText();
        Assert.DoesNotContain(AwsAccessKey, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", stored, StringComparison.Ordinal);
        Assert.Equal(draft.Id, artifact.Id);
        Assert.Equal(job.Id, artifact.JobId);
        Assert.Equal(job.Attempt, artifact.Attempt);
        Assert.Equal(ArtifactKind.RetrievedItem, artifact.Kind);
        Assert.Equal("source:2026.09.01:src/Checkout.cs", artifact.DomainRef);
        Assert.Equal(Now, artifact.CreatedAtUtc);
        Assert.Equal(
            CanonicalJsonSerializer.ComputeSha256Hex(
                CanonicalJsonSerializer.Canonicalize(JsonNode.Parse(stored)!)),
            artifact.ContentHash);
        Assert.True(artifact.RedactionApplied);
    }

    /// <summary>
    /// Nothing credential-shaped means nothing removed, and the row has to say so. A report built on
    /// this excerpt must not tell its reader that part of the evidence was withheld.
    /// </summary>
    [Fact]
    public void Create_RecordsThatNothingWasRedacted_WhenTheExcerptHoldsNoSecret()
    {
        var draft = new ToolArtifactDraft(
            ArtifactKind.RetrievedItem,
            "source:2026.09.01:src/Checkout.cs",
            SourcePayload(OrdinarySourceExcerpt));

        var artifact = RedactedToolArtifactFactory.Create(Job(), draft, Redaction(), Now);

        Assert.False(artifact.RedactionApplied);
    }

    /// <summary>
    /// The spoofing case. Connector text is attacker-controlled, and after redaction a value the
    /// redactor replaced is byte-identical to source text that already spelled out the placeholder.
    /// Anyone deriving the marker by searching the stored payload for that literal would report this
    /// artifact as redacted; the recorded outcome is taken against the pre-redaction document
    /// instead, so text that survived the pass untouched is reported as untouched however it reads.
    /// </summary>
    [Fact]
    public void Create_RecordsThatNothingWasRedacted_WhenSourceTextAlreadySpellsOutThePlaceholder()
    {
        var draft = new ToolArtifactDraft(
            ArtifactKind.RetrievedItem,
            "source:2026.09.01:src/Checkout.cs",
            SourcePayload("// audit note: the previous maintainer wrote [REDACTED] here on purpose."));

        var artifact = RedactedToolArtifactFactory.Create(Job(), draft, Redaction(), Now);

        Assert.Contains("[REDACTED]", artifact.RedactedPayload.GetRawText(), StringComparison.Ordinal);
        Assert.False(artifact.RedactionApplied);
    }

    /// <summary>
    /// The deliberate trade-off recorded in <c>docs/trade-offs.md</c>: source excerpts take the same
    /// redaction pass as every other tool payload, and the cost is paid only by the lines that look
    /// like credentials. Everything else, including the rest of the line after the connection-string
    /// delimiter, still reaches the model.
    /// </summary>
    [Fact]
    public void Create_ConfinesCredentialRedactionToTheLinesThatLookLikeCredentials()
    {
        var artifact = RedactedToolArtifactFactory.Create(
            Job(),
            new ToolArtifactDraft(
                ArtifactKind.RetrievedItem,
                "source:2026.09.01:src/Checkout.cs",
                SourcePayload(CredentialBearingSourceExcerpt)),
            Redaction(),
            Now);

        var excerpt = artifact.RedactedPayload.GetProperty("excerpt").GetString()!;
        Assert.Equal(
            CredentialBearingSourceExcerpt.Split('\n').Length,
            excerpt.Split('\n').Length);
        Assert.DoesNotContain(AwsAccessKey, excerpt, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", excerpt, StringComparison.Ordinal);
        Assert.DoesNotContain("expectedHash", excerpt, StringComparison.Ordinal);
        Assert.Contains("private const string LegacyAccessKey", excerpt, StringComparison.Ordinal);
        Assert.Contains("Database=checkout", excerpt, StringComparison.Ordinal);
        Assert.Contains(
            "logger.LogInformation(\"checkout retry {Attempt}\", attempt);",
            excerpt,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Create_LeavesOrdinarySourceExcerptsByteIdentical()
    {
        var artifact = RedactedToolArtifactFactory.Create(
            Job(),
            new ToolArtifactDraft(
                ArtifactKind.RetrievedItem,
                "source:2026.09.01:src/CheckoutHandler.cs",
                SourcePayload(OrdinarySourceExcerpt)),
            Redaction(),
            Now);

        Assert.Equal(OrdinarySourceExcerpt, artifact.RedactedPayload.GetProperty("excerpt").GetString());
    }

    /// <summary>
    /// The pattern rules only ever rewrite string values, so every other kind in the document comes
    /// back as it went in. The property-name denylist is the one part of the pass that does not
    /// preserve kinds, and it is pinned separately below.
    /// </summary>
    [Fact]
    public void Create_KeepsNonSensitiveKeysAtTheirOriginalValueKinds()
    {
        var payload = new JsonObject
        {
            ["evidenceKind"] = "ExistingTicket",
            ["issueNumber"] = 42,
            ["score"] = 0.85,
            ["resolved"] = false,
            ["assignee"] = null,
            ["labels"] = new JsonArray("sev1", "reported by " + ReporterEmail),
            ["origin"] = new JsonObject { ["summary"] = "leaked " + AwsAccessKey }
        };

        var artifact = RedactedToolArtifactFactory.Create(
            Job(),
            new ToolArtifactDraft(ArtifactKind.RetrievedItem, "ticket:github:owner/repo:42", payload),
            Redaction(),
            Now);

        using var parsed = JsonDocument.Parse(artifact.RedactedPayload.GetRawText());
        var root = parsed.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(42, root.GetProperty("issueNumber").GetInt32());
        Assert.Equal(0.85, root.GetProperty("score").GetDouble());
        Assert.Equal(JsonValueKind.False, root.GetProperty("resolved").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("assignee").ValueKind);
        Assert.Equal("sev1", root.GetProperty("labels")[0].GetString());
        Assert.Equal("reported by [REDACTED_EMAIL]", root.GetProperty("labels")[1].GetString());
        Assert.DoesNotContain(
            AwsAccessKey,
            root.GetProperty("origin").GetProperty("summary").GetString()!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The deliberate exception to kind preservation. A property whose name looks like a secret
    /// holder loses its value whatever kind that value had, so a number, a boolean, an array and a
    /// nested object all come back as the string <c>[REDACTED]</c>. Keeping this costs a false
    /// positive - <c>tokenCount</c> is a count, not a token - and it is kept anyway: a rule that only
    /// fired on strings would be defeated by sending the secret as a number or wrapping it in an
    /// object, and a payload's key names are not always backend-authored. A <c>WorkerOutput</c>
    /// payload's keys come from the model, which is what makes this reachable rather than theoretical.
    /// </summary>
    [Fact]
    public void Create_RedactsASuspiciouslyNamedFieldWhateverKindItsValueHas()
    {
        var payload = new JsonObject
        {
            ["tokenCount"] = 42,
            ["sessionCount"] = 7,
            ["hasApiKey"] = true,
            ["cookieNames"] = new JsonArray("sid", "csrf"),
            ["connectionString"] = new JsonObject { ["host"] = "db", ["port"] = 5432 },
            ["lineStart"] = 10
        };

        var artifact = RedactedToolArtifactFactory.Create(
            Job(),
            new ToolArtifactDraft(ArtifactKind.WorkerOutput, "worker:analysis", payload),
            Redaction(),
            Now);

        var root = artifact.RedactedPayload;
        Assert.Equal("[REDACTED]", root.GetProperty("tokenCount").GetString());
        Assert.Equal("[REDACTED]", root.GetProperty("hasApiKey").GetString());
        Assert.Equal("[REDACTED]", root.GetProperty("cookieNames").GetString());
        Assert.Equal("[REDACTED]", root.GetProperty("connectionString").GetString());
        // The matcher is segment-based, so the broad whole-name term does not fire on `sessionCount`
        // and an ordinary numeric field keeps its kind.
        Assert.Equal(7, root.GetProperty("sessionCount").GetInt32());
        Assert.Equal(10, root.GetProperty("lineStart").GetInt32());
        // Whatever it did to the kinds, the row is still a JSON object with exactly its own keys.
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(payload.Count, root.EnumerateObject().Count());
    }

    /// <summary>
    /// A payload can reach the redactor more than once: the same connector text is stored as a
    /// retrieved item and again inside the tool result, and the worker output path hands the stored
    /// document to the orchestrator after redacting it. A second pass must not rewrite what the first
    /// pass already marked.
    /// </summary>
    [Fact]
    public void Create_IsIdempotent()
    {
        var settings = Redaction();
        var payload = new JsonObject
        {
            ["excerpt"] = CredentialBearingSourceExcerpt,
            ["reportedBy"] = ReporterEmail,
            ["header"] = "Bearer abcdef1234567890ghijklmnop"
        };

        var once = RedactedToolArtifactFactory.Create(
            Job(), new ToolArtifactDraft(ArtifactKind.RetrievedItem, "source:r:src/A.cs", payload), settings, Now);
        var twice = RedactedToolArtifactFactory.Create(
            Job(),
            new ToolArtifactDraft(
                ArtifactKind.RetrievedItem,
                "source:r:src/A.cs",
                JsonNode.Parse(once.RedactedPayload.GetRawText())!),
            settings,
            Now);

        Assert.Equal(once.ContentHash, twice.ContentHash);
    }

    [Fact]
    public void RedactOutput_RedactsTheModelVisibleToolMessageToo()
    {
        var output = CanonicalJsonSerializer.ToElement(new JsonObject
        {
            ["matched"] = true,
            ["items"] = new JsonArray(new JsonObject
            {
                ["quote"] = "contact " + ReporterEmail + " and use " + AwsAccessKey
            })
        });

        var redacted = RedactedToolArtifactFactory.RedactOutput(output, Redaction());

        var text = redacted.Output.GetRawText();
        Assert.True(redacted.Output.GetProperty("matched").GetBoolean());
        Assert.True(redacted.RedactionApplied);
        Assert.DoesNotContain(ReporterEmail, text, StringComparison.Ordinal);
        Assert.DoesNotContain(AwsAccessKey, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tool output becomes the durable <c>ToolResult</c> artifact, which is citable evidence, so
    /// this outcome has to be as trustworthy as the per-item one. Nothing credential-shaped in, and
    /// nothing claimed: a report built only on this tool result must not tell its reader that part
    /// of the evidence was withheld.
    /// </summary>
    [Fact]
    public void RedactOutput_RecordsThatNothingWasRedacted_WhenTheOutputHoldsNoSecret()
    {
        var output = CanonicalJsonSerializer.ToElement(new JsonObject
        {
            ["matched"] = true,
            ["items"] = new JsonArray(new JsonObject { ["quote"] = OrdinarySourceExcerpt })
        });

        var redacted = RedactedToolArtifactFactory.RedactOutput(output, Redaction());

        Assert.False(redacted.RedactionApplied);
    }

    /// <summary>
    /// The spoofing case for the tool message, matching the one on the per-item artifact. Connector
    /// text that already spells out the placeholder survives the pass unchanged, and the recorded
    /// outcome is taken against the pre-redaction document rather than by looking for that literal
    /// in the result, so the ticket's author cannot raise the marker by writing it.
    /// </summary>
    [Fact]
    public void RedactOutput_RecordsThatNothingWasRedacted_WhenTheOutputAlreadySpellsOutThePlaceholder()
    {
        var output = CanonicalJsonSerializer.ToElement(new JsonObject
        {
            ["matched"] = true,
            ["items"] = new JsonArray(new JsonObject
            {
                ["quote"] = "the reporter wrote [REDACTED] into the ticket body on purpose"
            })
        });

        var redacted = RedactedToolArtifactFactory.RedactOutput(output, Redaction());

        Assert.Contains("[REDACTED]", redacted.Output.GetRawText(), StringComparison.Ordinal);
        Assert.False(redacted.RedactionApplied);
    }

    /// <summary>
    /// A failed tool's error message is the other thing a tool hands back, and it reaches the same two
    /// surfaces the output does: the model's tool message this turn and the <c>ToolResult</c> ledger
    /// rationale. No shipped tool builds it out of connector text, which is exactly why the rule has
    /// to live in the executor rather than in each tool's own good behaviour.
    /// </summary>
    [Fact]
    public void RedactReason_RemovesASecretFromAFailedToolsErrorMessage()
    {
        var redacted = RedactedToolArtifactFactory.RedactReason(
            "ticket provider rejected " + AwsAccessKey + " raised by " + ReporterEmail,
            Redaction());

        Assert.DoesNotContain(AwsAccessKey, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(ReporterEmail, redacted, StringComparison.Ordinal);
        Assert.StartsWith("ticket provider rejected ", redacted, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cost of redacting every tool payload is bounded by compiling each snapshot's configured
    /// patterns once, not once per payload or per field. A large excerpt must not change that.
    /// </summary>
    [Fact]
    public void Create_CompilesConfiguredPatternsOncePerConfigurationSnapshot()
    {
        var settings = Redaction();
        var largeExcerpt = string.Join('\n', Enumerable.Repeat(OrdinarySourceExcerpt, 10));
        Assert.False(CompiledRedactionPatterns.TryGetExisting(settings, out _));

        var first = RedactedToolArtifactFactory.Create(
            Job(), new ToolArtifactDraft(ArtifactKind.RetrievedItem, "source:r:src/A.cs", SourcePayload(largeExcerpt)), settings, Now);
        Assert.True(CompiledRedactionPatterns.TryGetExisting(settings, out var compiled));
        RedactedToolArtifactFactory.Create(
            Job(), new ToolArtifactDraft(ArtifactKind.RetrievedItem, "source:r:src/B.cs", SourcePayload(largeExcerpt)), settings, Now);
        Assert.True(CompiledRedactionPatterns.TryGetExisting(settings, out var reused));

        Assert.Same(compiled, reused);
        Assert.Equal(largeExcerpt, first.RedactedPayload.GetProperty("excerpt").GetString());
    }

    [Fact]
    public async Task SourceLookupToolDraft_LosesItsSecretOnTheWayToTheArtifact()
    {
        var tool = new SourceLookupTool(new StubSourceLookup(new SourceLookupResult(
            SourceLookupOutcome.Matched,
            "source_match",
            [new SourceLookupMatch("src/Checkout.cs", 1, 6, CredentialBearingSourceExcerpt, "2026.09.01", "heuristic")],
            [])));

        var stored = await ExecuteAndStoreAsync(tool, "source", "source_lookup", "{}");

        Assert.DoesNotContain(AwsAccessKey, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", stored, StringComparison.Ordinal);
        Assert.Contains("src/Checkout.cs", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TicketSearchToolDraft_LosesItsSecretOnTheWayToTheArtifact()
    {
        var tool = new TicketSearchTool(new StubTicketSearch(new TicketSearchResult(
            TicketSearchOutcome.Matched,
            "ticket_search_matches",
            [new TicketSearchMatch(
                "github",
                "owner/repo",
                "42",
                "Rotate " + AwsAccessKey + " reported by " + ReporterEmail,
                "open",
                "octocat",
                Now,
                "https://github.example/owner/repo/issues/42",
                0.85)],
            "github",
            "owner/repo")));

        var stored = await ExecuteAndStoreAsync(tool, "tickets", "ticket_search", "{}");

        Assert.DoesNotContain(AwsAccessKey, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(ReporterEmail, stored, StringComparison.Ordinal);
        Assert.Contains("owner/repo", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemorySearchToolDraft_LosesItsSecretOnTheWayToTheArtifact()
    {
        var tool = new MemorySearchTool(
            new StubEmbeddingClient(),
            new StubMemoryRepository([new MemorySearchMatch(
                Guid.Parse("10000000-0000-0000-0000-000000000001"),
                Guid.Parse("20000000-0000-0000-0000-000000000001"),
                "runbook",
                "runbooks/checkout.md",
                "Checkout runbook",
                0,
                "checkout timeout runbook; escalate to " + ReporterEmail + " using " + AwsAccessKey,
                0.9,
                "checkout-api",
                null,
                "2.4.0")]));

        var stored = await ExecuteAndStoreAsync(
            tool, "memory", "memory_search", "{\"query\":\"checkout timeout\"}");

        Assert.DoesNotContain(AwsAccessKey, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(ReporterEmail, stored, StringComparison.Ordinal);
        Assert.Contains("Checkout runbook", stored, StringComparison.Ordinal);
    }

    private static async Task<string> ExecuteAndStoreAsync(
        IImmediateAgentTool tool,
        string roleName,
        string toolName,
        string argumentsJson)
    {
        var configuration = Configuration();
        var job = Job();
        var validation = tool.Validate(Json(argumentsJson));
        Assert.True(validation.IsValid);
        var execution = await tool.ExecuteAsync(
            new AgentToolExecutionContext(
                job, configuration, roleName, toolName, "tenant-a", "checkout-api")
            {
                TriggerSignal = TriggerSignal(),
                FaultFingerprint = "fingerprint"
            },
            validation.SanitizedArguments,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolExecutionStatus.Succeeded, execution.Status);
        var draft = Assert.Single(execution.Artifacts!);
        var artifact = RedactedToolArtifactFactory.Create(job, draft, configuration.Redaction, Now);
        return artifact.RedactedPayload.GetRawText() +
            RedactedToolArtifactFactory.RedactOutput(execution.Output, configuration.Redaction).Output.GetRawText();
    }

    private static DateTimeOffset Now { get; } =
        DateTimeOffset.Parse("2026-01-15T00:00:00Z", CultureInfo.InvariantCulture);

    private static JsonObject SourcePayload(string excerpt) => new()
    {
        ["evidenceKind"] = "SourceCode",
        ["relativePath"] = "src/Checkout.cs",
        ["lineStart"] = 1,
        ["lineEnd"] = 6,
        ["excerpt"] = excerpt,
        ["release"] = "2026.09.01",
        ["mappingMethod"] = "heuristic"
    };

    /// <summary>
    /// A fresh instance every call: compiled patterns are cached per settings instance, and one test
    /// asserts on the first compilation of a snapshot it has not used before.
    /// </summary>
    private static RedactionSettings Redaction() => new(
        [],
        [new RedactionPatternSettings(
            "email",
            @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
            "[REDACTED_EMAIL]",
            IgnoreCase: true)],
        []);

    private static TriageConfiguration Configuration() => TestTriageConfiguration.Create() with
    {
        Redaction = Redaction(),
        CurrentReleases = new Dictionary<string, string>(StringComparer.Ordinal) { ["checkout-api"] = "2.4.0" }
    };

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

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Both the source and the ticket tool read backend-owned fields off the trigger signal, so the
    /// stack frame and the component label below are what makes each tool reach its connector at all.
    /// </summary>
    private static Signal TriggerSignal() => new(
        Guid.Parse("90000000-0000-0000-0000-000000000003"), "tenant-a", "otel", null, null, null,
        FingerprintStrength.Strong, true, null, false, null, null, null, null, null,
        "checkout-api", "prod", null, "Error", "TimeoutException", "Payment timed out", "summary",
        null, null, null, null, null,
        Json("""
            {
              "exception.stacktrace": "   at Example.Run() in src/Checkout.cs:line 11",
              "service.component": "checkout-api",
              "incident.labels": ["sev1", "payments"]
            }
            """),
        Json("{}"),
        Now,
        Now,
        null);

    private sealed class StubSourceLookup(SourceLookupResult result) : ISourceContextLookup
    {
        public Task<SourceLookupResult> LookupAsync(
            SourceLookupRequest request,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class StubTicketSearch(TicketSearchResult result) : ITicketSearch
    {
        public Task<TicketSearchResult> SearchAsync(
            TicketSearchRequest request,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class StubEmbeddingClient : IEmbeddingClient
    {
        public Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EmbeddingResponse([1f, 0f], request.Model, "mock", 2, request.CorrelationId));
    }

    private sealed class StubMemoryRepository(IReadOnlyList<MemorySearchMatch> matches) : IMemoryRepository
    {
        public Task<IReadOnlyList<MemorySearchMatch>> SearchAsync(
            MemorySearchRequest request,
            CancellationToken cancellationToken) => Task.FromResult(matches);

        public Task<bool> SeedItemExistsAsync(
            string owner,
            MemorySeedItem item,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReconcileSeedCorpusAsync(
            MemorySeedCorpus corpus,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MemoryCorpusInventory> GetCorpusInventoryAsync(
            string tenantId,
            string owner,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
