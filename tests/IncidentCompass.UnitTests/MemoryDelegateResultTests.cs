using System.Text.Json;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Infrastructure.Investigation;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The delegate result is the only channel by which a document retrieved mid-run reaches the
/// orchestrator: the orchestrator prompt is built once at claim time from the job's artifacts, so a
/// <c>RetrievedItem</c> created later by <c>memory_search</c> is never injected into it. Whatever
/// this factory drops, the orchestrator never sees.
/// </summary>
public sealed class MemoryDelegateResultTests
{
    private const string WorkerOutput = """
        {
          "matched": true,
          "items": [
            {
              "artifactId": "3f7e4b89-6d64-49dc-bb7e-0e9a5c7bde10",
              "title": "Checkout Timeout Runbook",
              "quote": "Checkout timeout alerts usually indicate upstream payment latency.",
              "score": 0.92,
              "documentationStatus": "Stale"
            },
            {
              "artifactId": "6a1c0d22-5b7f-4f6e-9a2d-2c4e8f0b1d33",
              "title": "Known Incident: Checkout Inventory Timeout",
              "quote": "Inventory request latency increased after a connection-pool change.",
              "score": 0.81,
              "documentationStatus": "Current"
            }
          ]
        }
        """;

    [Fact]
    public void MemoryDelegateResult_CarriesEachDocumentsBackendStatusUnderTheNameTheInstructionsUse()
    {
        var result = WorkerDelegateResultFactory.Create("memory", WorkerOutput, Guid.NewGuid());

        using var document = JsonDocument.Parse(result.SerializedPayload);
        var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal(2, items.Length);
        Assert.Equal(["Stale", "Current"], items.Select(static item => item.GetProperty("documentationStatus").GetString()));
        // The orchestrator is told to read camelCase keys, so these are the keys that must arrive.
        Assert.All(items, item => Assert.Equal(
            ["artifactId", "title", "quote", "score", "documentationStatus"],
            item.EnumerateObject().Select(static property => property.Name)));
    }

    [Fact]
    public void AnAbsentStatus_ArrivesAsAbsentRatherThanAsACurrentDocument()
    {
        const string withoutStatus = """
            {"matched":true,"items":[{"artifactId":"a","title":"t","quote":"q","score":0.5}]}
            """;

        var result = WorkerDelegateResultFactory.Create("memory", withoutStatus, Guid.NewGuid());

        using var document = JsonDocument.Parse(result.SerializedPayload);
        var item = document.RootElement.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, item.GetProperty("documentationStatus").ValueKind);
        Assert.Equal(
            DocumentationFitStatus.Missing,
            DocumentationFitCalculator.Resolve([item.GetProperty("documentationStatus").GetString()]));
    }

    /// <summary>
    /// The point of carrying the label: what the orchestrator can now compute from the delegate
    /// result is the same value the backend will derive from the artifacts behind those same
    /// documents and refuse the report over. Both sides are run here and compared.
    /// </summary>
    [Fact]
    public void WhatTheOrchestratorCanNowComputeEqualsWhatTheBackendWillDerive()
    {
        var result = WorkerDelegateResultFactory.Create("memory", WorkerOutput, Guid.NewGuid());
        using var document = JsonDocument.Parse(result.SerializedPayload);
        var orchestratorVisible = document.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(static item => item.GetProperty("documentationStatus").GetString())
            .ToArray();

        var orchestratorCanCompute = DocumentationFitCalculator.Resolve(orchestratorVisible);
        var backendEvidence = orchestratorVisible
            .Select(static status => new GroundedReportEvidence(
                Guid.NewGuid(), "RetrievedItem", "artifact:x", null, null, Guid.NewGuid(), status))
            .ToArray();

        // The resolver accepts exactly the value it derived, so a report carrying the orchestrator's
        // computed value passes rather than being refused.
        var applied = new PostgresDocumentationFitResolver().ValidateAndApply(
            Report(orchestratorCanCompute),
            backendEvidence);

        Assert.Equal(DocumentationFitStatus.CurrentWithHistorical, orchestratorCanCompute);
        Assert.Equal(orchestratorCanCompute, applied.DocumentationFit);
    }

    private static TriageReport Report(DocumentationFitStatus documentationFit) =>
        new(
            TriageReportStatus.Completed,
            "Checkout requests are timing out.",
            "KnownIncident",
            "Medium",
            [new TriageReportEvidenceReference("artifact:00000000-0000-0000-0000-000000000001", null)],
            [],
            "Follow the cited runbook.")
        {
            DocumentationFit = documentationFit
        };
}
