using System.Text.Json;
using IncidentCompass.Application.Investigation.Reports;

namespace IncidentCompass.UnitTests;

/// <summary>
/// A report's model provenance is stored as jsonb on the immutable report row and served as
/// <c>modelProvenance</c> on the report details response, so this element shape is a persisted
/// contract and a public one at the same time. Reports cannot be rewritten, so a renamed or
/// reordered property would leave already-published rows unreadable rather than merely stale.
/// </summary>
public sealed class TriageReportModelParticipantTests
{
    [Fact]
    public void Serialize_PinsTheExactPersistedJsonForAWorkerParticipant()
    {
        var participant = new TriageReportModelParticipant(
            CallKind: "worker",
            Role: "analysis",
            RouteId: "analysis-chat",
            Provider: "openai-compatible",
            Model: "local-worker-model",
            CallCount: 2,
            ProviderId: "local-oai");

        const string expected = """
            {"callKind":"worker","role":"analysis","routeId":"analysis-chat","provider":"openai-compatible","model":"local-worker-model","callCount":2,"providerId":"local-oai"}
            """;

        Assert.Equal(expected, JsonSerializer.Serialize(participant));
    }

    [Fact]
    public void Serialize_KeepsAnOrchestratorRoleExplicitlyNullRatherThanOmittingIt()
    {
        var participant = new TriageReportModelParticipant(
            CallKind: "orchestrator",
            Role: null,
            RouteId: "report-chat",
            Provider: "openai-compatible",
            Model: "local-orchestrator-model",
            CallCount: 3,
            ProviderId: "local-oai");

        const string expected = """
            {"callKind":"orchestrator","role":null,"routeId":"report-chat","provider":"openai-compatible","model":"local-orchestrator-model","callCount":3,"providerId":"local-oai"}
            """;

        Assert.Equal(expected, JsonSerializer.Serialize(participant));
    }

    /// <summary>
    /// A report published before the configured provider was recorded reads back with that property
    /// null, which is a different claim from naming a provider and must not become a read failure:
    /// reports are immutable, so those rows can never be backfilled.
    /// </summary>
    [Fact]
    public void Deserialize_ReadsARowStoredBeforeTheConfiguredProviderWasRecorded()
    {
        const string stored = """
            {"callKind":"orchestrator","role":null,"routeId":"report-chat","provider":"local-oai","model":"local-orchestrator-model","callCount":3}
            """;

        var participant = JsonSerializer.Deserialize<TriageReportModelParticipant>(stored);

        Assert.Equal("local-oai", participant!.Provider);
        Assert.Null(participant.ProviderId);
        Assert.Equal(3, participant.CallCount);
    }

    [Fact]
    public void RoundTrip_ReadsBackTheStoredShapeWithoutOptions()
    {
        var participants = new TriageReportModelParticipant[]
        {
            new("orchestrator", null, "report-chat", "local-oai", "local-orchestrator-model", 2),
            new("worker", "analysis", "analysis-chat", "local-oai", "local-worker-model", 1)
        };

        var stored = JsonSerializer.Serialize(participants);

        Assert.Equal(
            participants,
            JsonSerializer.Deserialize<IReadOnlyList<TriageReportModelParticipant>>(stored));
    }

    /// <summary>
    /// Two calls that differ only by model, or only by route, are two participants. That is what
    /// keeps the claim true if a later change lets one role run on more than one route.
    /// <para>
    /// The configured provider is part of that too, and it is the case the adapter identifier
    /// cannot cover: two providers a host declared with different endpoints and credentials answer
    /// under one adapter name, and without this they would be reported as a single participant.
    /// </para>
    /// </summary>
    [Fact]
    public void Equality_SeparatesParticipantsThatDifferOnlyByRouteModelOrConfiguredProvider()
    {
        var baseline = new TriageReportModelParticipant(
            "worker", "analysis", "analysis-chat", "openai-compatible", "local-worker-model", 1, "local-oai");

        Assert.NotEqual(baseline, baseline with { Model = "other-worker-model" });
        Assert.NotEqual(baseline, baseline with { RouteId = "second-analysis-chat" });
        Assert.NotEqual(baseline, baseline with { Provider = "other-provider" });
        Assert.NotEqual(baseline, baseline with { ProviderId = "second-local-oai" });
    }
}
