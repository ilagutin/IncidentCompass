using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Infrastructure.ModelGateway.Mock;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The mock profile ships with the same memory role instructions as the real one, so its scripted
/// memory worker has to follow them: an item banded <c>low</c> was admitted without being confirmed,
/// by the relevance judge or, on a host that runs no judge, by the vector-only fallback. Without this
/// the mock would turn an unconfirmed cross-language hit into a KnownIncident report, and the demo
/// profile would demonstrate the opposite of the shipped policy.
/// </summary>
public sealed class MockMemoryWorkerConfidenceTests
{
    [Theory]
    [InlineData("related matches, none confirmed by the relevance judge")]
    [InlineData("vector-only matches, not lexically confirmed")]
    public void MemoryWorkerResponse_UnconfirmedResultReturnsTheHonestEmptyResult(string message)
    {
        var output = RunMemoryWorker(ToolResult(
            message,
            ("a1", "Checkout Timeout Runbook", "low"),
            ("a2", "Inventory Latency Note", "low")));

        Assert.False(output["matched"]!.GetValue<bool>());
        Assert.Empty(output["items"]!.AsArray());
        Assert.Equal("no confirmed matches", output["noMatchReason"]!.GetValue<string>());
    }

    [Fact]
    public void MemoryWorkerResponse_MixedResultKeepsOnlyTheConfirmedItem()
    {
        var output = RunMemoryWorker(ToolResult(
            "matches found",
            ("a1", "Checkout Timeout Runbook", "medium"),
            ("a2", "Inventory Latency Note", "low")));

        Assert.True(output["matched"]!.GetValue<bool>());
        var item = Assert.Single(output["items"]!.AsArray());
        Assert.Equal("a1", item!["artifactId"]!.GetValue<string>());
        Assert.Null(output["noMatchReason"]);
    }

    [Fact]
    public void MemoryWorkerResponse_FullyConfirmedResultIsUnchanged()
    {
        var output = RunMemoryWorker(ToolResult(
            "matches found",
            ("a1", "Checkout Timeout Runbook", "high"),
            ("a2", "Inventory Latency Note", "medium")));

        Assert.True(output["matched"]!.GetValue<bool>());
        Assert.Equal(2, output["items"]!.AsArray().Count);
    }

    [Fact]
    public void MemoryWorkerResponse_EmptyToolResultKeepsTheToolsOwnNoMatchReason()
    {
        var output = RunMemoryWorker(new JsonObject
        {
            ["matched"] = false,
            ["message"] = "no matches",
            ["items"] = new JsonArray(),
            ["noMatchReason"] = "no matches"
        }.ToJsonString());

        Assert.False(output["matched"]!.GetValue<bool>());
        Assert.Equal("no matches", output["noMatchReason"]!.GetValue<string>());
    }

    private static JsonObject RunMemoryWorker(string toolResult)
    {
        var request = new AiModelRequest(
            "mock-memory-confidence",
            "mock-model",
            [
                new AiChatMessage(AiMessageRole.System, "memory worker"),
                new AiChatMessage(AiMessageRole.User, "Search memory for the fault."),
                new AiChatMessage(AiMessageRole.Assistant, string.Empty),
                new AiChatMessage(AiMessageRole.Tool, toolResult, "memory-search-1")
            ],
            Tools: [new AiToolDefinition("memory_search", "Search memory.", "v1", default)]);

        var response = MockIncidentCompassScripts.MemoryWorkerResponse(request, hasToolResult: true);

        return (JsonObject)JsonNode.Parse(response.Content)!;
    }

    private static string ToolResult(string message, params (string ArtifactId, string Title, string Band)[] items)
    {
        var array = new JsonArray();
        foreach (var (artifactId, title, band) in items)
        {
            array.Add(new JsonObject
            {
                ["artifactId"] = artifactId,
                ["title"] = title,
                ["quote"] = title + " quote",
                ["score"] = 0.87,
                ["retrievalConfidence"] = band,
                ["documentationStatus"] = "Unversioned"
            });
        }

        return new JsonObject
        {
            ["matched"] = true,
            ["message"] = message,
            ["items"] = array,
            ["noMatchReason"] = null
        }.ToJsonString();
    }
}
