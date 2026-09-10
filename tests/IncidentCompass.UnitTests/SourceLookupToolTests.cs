using System.Globalization;
using System.Text.Json;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.SourceContext;
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
        Assert.StartsWith("source:", draft.DomainRef, StringComparison.Ordinal);
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
