using System.Text.Json;
using IncidentCompass.Application.Investigation.Jobs;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The <c>ModelCall</c> rationale payload is persisted in the triage ledger and read back by the
/// hourly cost rollup, so naming it must not change a single byte of its serialized shape.
/// </summary>
public sealed class ModelCallLedgerMetadataTests
{
    private static readonly ModelCallLedgerMetadata Metadata = new(
        Kind: "orchestrator",
        RouteId: "report-chat",
        Model: "local-model",
        Provider: "local-oai",
        UsageSource: "provider",
        InputTokens: 128,
        OutputTokens: 64,
        TotalTokens: 192,
        DurationMs: 1234,
        ProposedToolCallCount: 1,
        CallId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Outcome: "success",
        ErrorCode: null);

    [Fact]
    public void Serialize_MatchesTheAnonymousPayloadShapeThatWroteExistingLedgerRows()
    {
        // Exactly the anonymous type that InvestigationModelCaller used before the payload was named.
        var legacy = new
        {
            kind = Metadata.Kind,
            routeId = Metadata.RouteId,
            model = Metadata.Model,
            provider = Metadata.Provider,
            usageSource = Metadata.UsageSource,
            inputTokens = Metadata.InputTokens,
            outputTokens = Metadata.OutputTokens,
            totalTokens = Metadata.TotalTokens,
            durationMs = Metadata.DurationMs,
            proposedToolCallCount = Metadata.ProposedToolCallCount,
            callId = Metadata.CallId,
            outcome = Metadata.Outcome,
            errorCode = Metadata.ErrorCode
        };

        Assert.Equal(JsonSerializer.Serialize(legacy), JsonSerializer.Serialize(Metadata));
    }

    [Fact]
    public void Serialize_PinsTheExactPersistedJson()
    {
        const string expected = """
            {"kind":"orchestrator","routeId":"report-chat","model":"local-model","provider":"local-oai","usageSource":"provider","inputTokens":128,"outputTokens":64,"totalTokens":192,"durationMs":1234,"proposedToolCallCount":1,"callId":"11111111-1111-1111-1111-111111111111","outcome":"success","errorCode":null}
            """;

        Assert.Equal(expected, JsonSerializer.Serialize(Metadata));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(48)]
    public void Serialize_AppendsExplicitProviderReasoningTokensAfterLegacyFields(int reasoningTokens)
    {
        var metadata = Metadata with { ReasoningTokens = reasoningTokens };
        var expected =
            "{\"kind\":\"orchestrator\",\"routeId\":\"report-chat\",\"model\":\"local-model\"," +
            "\"provider\":\"local-oai\",\"usageSource\":\"provider\",\"inputTokens\":128," +
            "\"outputTokens\":64,\"totalTokens\":192,\"durationMs\":1234,\"proposedToolCallCount\":1," +
            "\"callId\":\"11111111-1111-1111-1111-111111111111\",\"outcome\":\"success\"," +
            $"\"errorCode\":null,\"reasoningTokens\":{reasoningTokens}}}";

        Assert.Equal(expected, JsonSerializer.Serialize(metadata));
    }
}
