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
            Provider: "local-oai",
            Model: "local-worker-model",
            CallCount: 2);

        const string expected = """
            {"callKind":"worker","role":"analysis","routeId":"analysis-chat","provider":"local-oai","model":"local-worker-model","callCount":2}
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
            Provider: "local-oai",
            Model: "local-orchestrator-model",
            CallCount: 3);

        const string expected = """
            {"callKind":"orchestrator","role":null,"routeId":"report-chat","provider":"local-oai","model":"local-orchestrator-model","callCount":3}
            """;

        Assert.Equal(expected, JsonSerializer.Serialize(participant));
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
    /// </summary>
    [Fact]
    public void Equality_SeparatesParticipantsThatDifferOnlyByRouteOrModel()
    {
        var baseline = new TriageReportModelParticipant(
            "worker", "analysis", "analysis-chat", "local-oai", "local-worker-model", 1);

        Assert.NotEqual(baseline, baseline with { Model = "other-worker-model" });
        Assert.NotEqual(baseline, baseline with { RouteId = "second-analysis-chat" });
        Assert.NotEqual(baseline, baseline with { Provider = "other-provider" });
    }
}
