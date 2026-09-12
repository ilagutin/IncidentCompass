using System.Globalization;
using System.Text.Json;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.SourceContext;
using IncidentCompass.Domain.Governance;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.UnitTests;

public sealed class SourceLookupToolTests
{
    [Fact]
    public async Task Execute_UsesBackendServiceReleaseAndSignalAndCreatesClosedArtifact()
    {
        var adapter = new CapturingLookup(new SourceLookupResult(
            SourceLookupOutcome.Matched,
            "source_match",
            [new SourceLookupMatch("src/Checkout.cs", 10, 12, "line 10\nline 11\nline 12", "r1", "heuristic")],
            []));
        var tool = new SourceLookupTool(adapter);
        var context = CreateContext(includeRelease: true);

        var result = await tool.ExecuteAsync(context, Json("{}"), TestContext.Current.CancellationToken);

        Assert.Equal("checkout", adapter.Request!.ServiceName);
        Assert.Equal("r1", adapter.Request.Release);
        Assert.Equal("src/Checkout.cs", Assert.Single(adapter.Request.Frames).Path);
        Assert.True(result.Output.GetProperty("matched").GetBoolean());
        var draft = Assert.Single(result.Artifacts!);
        Assert.Equal(ArtifactKind.RetrievedItem, draft.Kind);
        Assert.StartsWith("source:", draft.DomainRef.Value, StringComparison.Ordinal);
        Assert.Equal("SourceCode", draft.Payload["evidenceKind"]!.GetValue<string>());
        Assert.Equal("r1", draft.Payload["release"]!.GetValue<string>());
        Assert.Equal("heuristic", draft.Payload["mappingMethod"]!.GetValue<string>());
    }

    [Fact]
    public async Task Execute_MissingCurrentReleaseReturnsDurableUnavailableShapeWithoutAdapterCall()
    {
        var adapter = new CapturingLookup(SourceLookupResult.NoMatch("unused"));
        var tool = new SourceLookupTool(adapter);

        var result = await tool.ExecuteAsync(
            CreateContext(includeRelease: false),
            Json("{}"),
            TestContext.Current.CancellationToken);

        Assert.Null(adapter.Request);
        Assert.Equal("connector_unavailable", result.Output.GetProperty("outcome").GetString());
        Assert.Equal("source_release_unavailable", result.Output.GetProperty("code").GetString());
        Assert.Empty(result.Artifacts!);
    }

    /// <summary>
    /// A repository-relative path past the domain-reference segment cap is something a deep checkout
    /// produces on its own, with nothing misconfigured and nothing hostile: the source read boundary
    /// bounds frames, candidate files, bytes, excerpt lines and extensions, and none of those is a
    /// path length. So the tool has to keep working. It drops the one match it cannot name, says so
    /// through the limitation list the read boundary already uses, and returns the rest - rather than
    /// throwing an exception that no tool-failure branch, role runner or retry classifier on the
    /// worker path matches, which would fail the attempt identically on every retry and dead-letter
    /// the job with the ledger showing a tool call proposed and allowed and no outcome.
    /// </summary>
    [Fact]
    public async Task Execute_DropsAMatchWhoseReferenceCannotBeBuiltAndReportsALimitation()
    {
        var unnameablePath = "src/" + new string('a', 400) + ".cs";
        var adapter = new CapturingLookup(new SourceLookupResult(
            SourceLookupOutcome.Matched,
            "source_match",
            [
                new SourceLookupMatch(unnameablePath, 10, 12, "line 10", "r1", "heuristic"),
                new SourceLookupMatch("src/Checkout.cs", 10, 12, "line 10", "r1", "heuristic")
            ],
            []));
        var tool = new SourceLookupTool(adapter);

        var result = await tool.ExecuteAsync(
            CreateContext(includeRelease: true),
            Json("{}"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        var draft = Assert.Single(result.Artifacts!);
        Assert.Equal("source:r1:src/Checkout.cs", draft.DomainRef.Value);
        var item = Assert.Single(result.Output.GetProperty("items").EnumerateArray());
        Assert.Equal("src/Checkout.cs", item.GetProperty("relativePath").GetString());
        Assert.Equal(draft.Id.ToString(), item.GetProperty("artifactId").GetString());
        var limitation = Assert.Single(result.Output.GetProperty("limitations").EnumerateArray());
        Assert.Equal("source_reference_rejected", limitation.GetString());
        Assert.True(result.Output.GetProperty("matched").GetBoolean());
    }

    /// <summary>
    /// The limitation says that a reference could not be built, which is one fact however many
    /// matches it happened to, so several drops in one call still produce one code. A code repeated
    /// once per drop would read to the model as several distinct problems.
    /// </summary>
    [Fact]
    public async Task Execute_ReportsTheUnrepresentableReferenceLimitationOnceForSeveralDrops()
    {
        var adapter = new CapturingLookup(new SourceLookupResult(
            SourceLookupOutcome.Matched,
            "source_match",
            [
                new SourceLookupMatch(
                    "src/" + new string('a', 400) + ".cs", 10, 12, "line 10", "r1", "heuristic"),
                new SourceLookupMatch(
                    "src/" + new string('b', 400) + ".cs", 20, 22, "line 20", "r1", "heuristic"),
                new SourceLookupMatch("src/Checkout.cs", 10, 12, "line 10", "r1", "heuristic")
            ],
            []));
        var tool = new SourceLookupTool(adapter);

        var result = await tool.ExecuteAsync(
            CreateContext(includeRelease: true),
            Json("{}"),
            TestContext.Current.CancellationToken);

        var codes = result.Output.GetProperty("limitations").EnumerateArray()
            .Select(item => item.GetString())
            .ToList();
        Assert.Equal("source_reference_rejected", Assert.Single(codes));
        Assert.Single(result.Artifacts!);
    }

    /// <summary>
    /// The degenerate end of the same case: when no match can be named there is nothing to ground a
    /// report on, so the tool reports that rather than claiming a match it did not return.
    /// </summary>
    [Fact]
    public async Task Execute_ReportsNoMatchWhenEveryReferenceIsRefused()
    {
        var unnameablePath = "src/" + new string('a', 400) + ".cs";
        var adapter = new CapturingLookup(new SourceLookupResult(
            SourceLookupOutcome.Matched,
            "source_match",
            [new SourceLookupMatch(unnameablePath, 10, 12, "line 10", "r1", "heuristic")],
            []));
        var tool = new SourceLookupTool(adapter);

        var result = await tool.ExecuteAsync(
            CreateContext(includeRelease: true),
            Json("{}"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Empty(result.Artifacts!);
        Assert.False(result.Output.GetProperty("matched").GetBoolean());
        Assert.Equal(
            "source_reference_rejected",
            Assert.Single(result.Output.GetProperty("limitations").EnumerateArray()).GetString());
    }

    [Fact]
    public void Validate_RejectsAnyModelSelectedArgument()
    {
        var tool = new SourceLookupTool(new CapturingLookup(SourceLookupResult.NoMatch("unused")));

        var validation = tool.Validate(Json("{\"path\":\"secret.cs\"}"));

        Assert.False(validation.IsValid);
    }

    private static AgentToolExecutionContext CreateContext(bool includeRelease)
    {
        var now = DateTimeOffset.Parse("2026-01-15T00:00:00Z", CultureInfo.InvariantCulture);
        var job = new TriageJob(
            Guid.NewGuid(), Guid.NewGuid(), TriageJobStatus.Processing, 1, "worker", now.AddMinutes(1),
            null, null, null, "hash", now, now);
        var configuration = TestTriageConfiguration.Create() with
        {
            CurrentReleases = includeRelease
                ? new Dictionary<string, string>(StringComparer.Ordinal) { ["checkout"] = "r1" }
                : new Dictionary<string, string>(StringComparer.Ordinal)
        };
        return new AgentToolExecutionContext(job, configuration, "source", "source_lookup", "tenant", "checkout")
        {
            TriggerSignal = CreateSignal(now),
            FaultFingerprint = "fingerprint"
        };
    }

    private static Signal CreateSignal(DateTimeOffset now) => new(
        Guid.NewGuid(), "tenant", "otel", null, null, null, FingerprintStrength.Strong, true,
        null, false, null, null, null, null, null, "checkout", "test", null, "Error",
        "ExampleException", "message", "summary", null, null, null, null, null,
        JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            ["exception.stacktrace"] = "   at Example.Run() in src/Checkout.cs:line 11"
        }),
        Json("{}"), now, now, null);

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class CapturingLookup(SourceLookupResult result) : ISourceContextLookup
    {
        public SourceLookupRequest? Request { get; private set; }

        public Task<SourceLookupResult> LookupAsync(SourceLookupRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return Task.FromResult(result);
        }
    }
}
