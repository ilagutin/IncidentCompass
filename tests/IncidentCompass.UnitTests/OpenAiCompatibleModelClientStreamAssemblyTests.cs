using System.Net;
using System.Net.Http.Headers;
using System.Text;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.ModelGateway.OpenAi;
using IncidentCompass.TestSupport;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

/// <summary>
/// A streamed answer is assembled into the completion a non-streamed answer would have carried and
/// validated by the same mapping, and a malformed or hostile stream ends with a named failure instead
/// of a partial answer. Each body is delivered in seven-byte reads, so events, lines and UTF-8
/// characters are split across reads throughout.
/// </summary>
public sealed class OpenAiCompatibleModelClientStreamAssemblyTests
{
    private const string Done = "data: [DONE]\n\n";

    [Fact]
    public async Task CompleteAsync_ToolCallsSplitAcrossInterleavedFragments_AssembleLikeTheJsonAnswer()
    {
        var streamed = await CompleteStreamAsync(
            Event("""{"model":"tool-model","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"source_lookup","arguments":""}}]}}]}"""),
            Event("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"pa"}}]}}]}"""),
            Event("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call_b","type":"function","function":{"name":"ticket_search","arguments":"{\"query\":"}}]}}]}"""),
            Event("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"src/é.cs\"}"}}]}}]}"""),
            Event("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"arguments":"\"timeout\"}"}}]}}]}"""),
            Event("""{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}"""),
            Done);
        var json = await CompleteJsonAsync(
            """{"model":"tool-model","choices":[{"message":{"content":null,"tool_calls":[{"id":"call_a","type":"function","function":{"name":"source_lookup","arguments":"{\"path\":\"src/é.cs\"}"}},{"id":"call_b","type":"function","function":{"name":"ticket_search","arguments":"{\"query\":\"timeout\"}"}}]},"finish_reason":"tool_calls"}]}""");

        Assert.Equal(json.Model, streamed.Model);
        Assert.Equal(json.Content, streamed.Content);
        var expectedCalls = Assert.IsAssignableFrom<IReadOnlyList<AiToolCall>>(json.ProposedToolCalls);
        var streamedCalls = Assert.IsAssignableFrom<IReadOnlyList<AiToolCall>>(streamed.ProposedToolCalls);
        Assert.Equal(2, expectedCalls.Count);
        Assert.Equal(expectedCalls.Count, streamedCalls.Count);
        for (var index = 0; index < expectedCalls.Count; index++)
        {
            Assert.Equal(expectedCalls[index].Id, streamedCalls[index].Id);
            Assert.Equal(expectedCalls[index].Name, streamedCalls[index].Name);
            Assert.Equal(expectedCalls[index].Arguments.GetRawText(), streamedCalls[index].Arguments.GetRawText());
        }

        Assert.Equal("src/é.cs", streamedCalls[0].Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public async Task CompleteAsync_AssembledToolCallArgumentsThatAreNotAnObject_AreRefused()
    {
        var exception = await FailStreamAsync(
            Event("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"source_lookup","arguments":"{\"path\":"}}]}}]}"""),
            Event("""{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}"""),
            Done);

        Assert.Equal(ProviderFailureKind.InvalidResponse, exception.FailureKind);
        Assert.Equal("invalid_response", exception.ErrorCode);
    }

    public static TheoryData<string, string> HostileToolCallFragments => new()
    {
        {
            "index gap",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call_b","type":"function","function":{"name":"x","arguments":"{}"}}]}}]}"""
        },
        {
            "unstarted index without id",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{}"}}]}}]}"""
        },
        {
            "missing index",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"id":"call_a","type":"function","function":{"name":"x","arguments":"{}"}}]}}]}"""
        },
        {
            "negative index",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":-1,"id":"call_a","type":"function","function":{"name":"x","arguments":"{}"}}]}}]}"""
        },
        {
            "second id for the same index",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"x","arguments":"{"}},{"index":0,"id":"call_z","function":{"arguments":"}"}}]}}]}"""
        },
        {
            "renamed function for the same index",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"x","arguments":"{"}},{"index":0,"function":{"name":"y","arguments":"}"}}]}}]}"""
        },
        {
            "null fragment",
            """{"choices":[{"index":0,"delta":{"tool_calls":[null]}}]}"""
        }
    };

    [Theory]
    [MemberData(nameof(HostileToolCallFragments))]
    public async Task CompleteAsync_ToolCallFragmentThatCannotBelongToAWellFormedCall_IsRefused(string scenario, string chunk)
    {
        var exception = await FailStreamAsync(
            Event(chunk),
            Event("""{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}"""),
            Done);

        Assert.True(exception.ErrorCode == "invalid_response", scenario);
        Assert.Equal(ProviderFailureKind.InvalidResponse, exception.FailureKind);
    }

    [Fact]
    public async Task CompleteAsync_ErrorEventMidStream_IsAFailureAfterDispatchWithoutTheProviderMessage()
    {
        var attempts = 0;
        var exception = await FailStreamAsync(
            () => attempts++,
            Event("""{"choices":[{"index":0,"delta":{"content":"partial"}}]}"""),
            Event("""{"error":{"message":"secret upstream detail","code":"server_overloaded"}}"""),
            Done);

        Assert.Equal(1, attempts);
        Assert.Equal(ProviderFailureKind.AmbiguousInterruption, exception.FailureKind);
        Assert.Equal("provider_dispatch_outcome_unknown", exception.ErrorCode);
        Assert.Equal("server_overloaded", exception.ProviderErrorCode);
        Assert.DoesNotContain("secret upstream detail", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("partial", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_ErrorEventOfAnUnexpectedShape_IsStillAFailureAfterDispatch()
    {
        var exception = await FailStreamAsync(Event("""{"error":"overloaded"}"""), Done);

        Assert.Equal("provider_dispatch_outcome_unknown", exception.ErrorCode);
        Assert.Null(exception.ProviderErrorCode);
    }

    public static TheoryData<string> TruncatedBodies => new()
    {
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"half an answer\"}}]}\n\n",
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"a\"}}]}\n\ndata: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n",
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"a\"}}]}\n\ndata: [DONE]",
        "data: {\"choices\":[{\"index\":1,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n",
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"no finish\"}}]}\n\ndata: [DONE]\n\n",
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"a\"}},{\"index\":1,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
        "error: {\"message\":\"llama.cpp style field line\"}\n\n",
        ""
    };

    /// <summary>
    /// A body that closes without <c>[DONE]</c> and without a finish reason for choice 0 was cut off,
    /// including when the event that would have finished it never received its closing blank line.
    /// </summary>
    [Theory]
    [MemberData(nameof(TruncatedBodies))]
    public async Task CompleteAsync_StreamThatEndsBeforeItFinished_IsADispatchWithUnknownOutcome(string body)
    {
        var attempts = 0;
        var exception = await FailStreamAsync(() => attempts++, body);

        Assert.Equal(1, attempts);
        Assert.Equal(ProviderFailureKind.AmbiguousInterruption, exception.FailureKind);
        Assert.Equal("provider_dispatch_outcome_unknown", exception.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_StreamThatFinishedButClosesWithoutDone_IsAccepted()
    {
        var response = await CompleteStreamAsync(
            Event("""{"choices":[{"index":0,"delta":{"content":"done"},"finish_reason":"stop"}]}"""));

        Assert.Equal("done", response.Content);
    }

    [Fact]
    public async Task CompleteAsync_DoneWithoutAUsageChunk_LeavesUsageAbsent()
    {
        var response = await CompleteStreamAsync(
            Event("""{"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}"""),
            Done);

        Assert.Equal("ok", response.Content);
        Assert.Null(response.Usage);
        Assert.Equal("test-model", response.Model);
    }

    [Fact]
    public async Task CompleteAsync_UsageOnTheSameChunkAsTheFinishReason_IsKept()
    {
        var response = await CompleteStreamAsync(
            Event("""{"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1,"total_tokens":4}}"""),
            Done);

        Assert.Equal(3, response.Usage?.InputTokens);
        Assert.Equal(4, response.Usage?.TotalTokens);
    }

    [Fact]
    public async Task CompleteAsync_NullUsageOnEveryChunkThenAUsageChunk_KeepsTheUsageChunk()
    {
        var response = await CompleteStreamAsync(
            Event("""{"choices":[{"index":0,"delta":{"content":"o"}}],"usage":null}"""),
            Event("""{"choices":[{"index":0,"delta":{"content":"k"},"finish_reason":"stop"}],"usage":null}"""),
            Event("""{"choices":[],"usage":{"prompt_tokens":5,"completion_tokens":2,"total_tokens":7}}"""),
            Event("""{"choices":[],"usage":null}"""),
            Done);

        Assert.Equal("ok", response.Content);
        Assert.Equal(5, response.Usage?.InputTokens);
        Assert.Equal(2, response.Usage?.OutputTokens);
    }

    [Fact]
    public async Task CompleteAsync_DoneBeforeAnyChoice_IsAnEmptyResponse()
    {
        var exception = await FailStreamAsync(Event("""{"model":"m","choices":[]}"""), Done);

        Assert.Equal("empty_response", exception.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_LengthFinishWithoutContent_IsTheOutputLimit()
    {
        var exception = await FailStreamAsync(
            Event("""{"choices":[{"index":0,"delta":{"reasoning_content":"thinking"}}]}"""),
            Event("""{"choices":[{"index":0,"delta":{},"finish_reason":"length"}]}"""),
            Event("""{"choices":[],"usage":{"prompt_tokens":1,"completion_tokens":9,"total_tokens":10}}"""),
            Done);

        Assert.Equal("provider_output_limit_reached", exception.ErrorCode);
        Assert.Equal(9, exception.Usage?.OutputTokens);
    }

    public static TheoryData<string> MalformedDataEvents => new()
    {
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"a\"\n\n",
        "data: null\n\n",
        "data: [1,2]\n\n",
        "data: not json\n\n"
    };

    [Theory]
    [MemberData(nameof(MalformedDataEvents))]
    public async Task CompleteAsync_DataEventThatIsNotAChunkObject_IsInvalidJson(string body)
    {
        var exception = await FailStreamAsync(body, Done);

        Assert.Equal(ProviderFailureKind.InvalidResponse, exception.FailureKind);
        Assert.Equal("invalid_json", exception.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_HostileFraming_AssemblesOnlyChoiceZeroDataAndIgnoresEverythingElse()
    {
        var response = await CompleteStreamAsync(
            "\uFEFF: comment first\r\n\r\n",
            "event: message\r\nid: 1\r\nretry: 10\r\ndata: {\"choices\":[{\"index\":1,\"delta\":{\"content\":\"other\"}},\r\n",
            "data: {\"index\":0,\"delta\":{\"content\":\"ü€\"}}]}\r\n\r\n",
            "data:{\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"hidden\",\"content\":\"𝄞\"},\"finish_reason\":\"stop\"}]}\r\r",
            "data\n\n",
            Done,
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"after done\"}}]}\n\n");

        Assert.Equal("ü€𝄞", response.Content);
    }

    private static string Event(string json) => "data: " + json + "\n\n";

    private static Task<AiModelResponse> CompleteStreamAsync(params string[] parts) =>
        CreateClient(StreamingHandler(() => { }, parts)).CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

    private static Task<AiModelException> FailStreamAsync(params string[] parts) =>
        FailStreamAsync(() => { }, parts);

    private static Task<AiModelException> FailStreamAsync(Action onAttempt, params string[] parts) =>
        Assert.ThrowsAsync<AiModelException>(() =>
            CreateClient(StreamingHandler(onAttempt, parts)).CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken));

    private static Task<AiModelResponse> CompleteJsonAsync(string json) =>
        CreateClient(new CallbackHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        })).CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

    private static CallbackHandler StreamingHandler(Action onAttempt, string[] parts) =>
        new(_ =>
        {
            onAttempt();
            var body = new ControlledResponseStream();
            var bytes = Encoding.UTF8.GetBytes(string.Concat(parts));
            for (var offset = 0; offset < bytes.Length; offset += 7)
            {
                body.Write(bytes[offset..Math.Min(bytes.Length, offset + 7)]);
            }

            body.Complete();
            var content = new StreamContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream") { CharSet = "utf-8" };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

    private static OpenAiCompatibleModelClient CreateClient(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            Options.Create(new OpenAiCompatibleModelClientOptions
            {
                BaseUrl = "https://provider.example",
                ApiKey = "test-api-key",
                MaxRetryAttempts = 3,
                RetryBaseDelayMilliseconds = 1
            }),
            TestModelProviderProfiles.CreateResolver(),
            new ManualTimerTimeProvider());

    private static AiModelRequest CreateRequest() =>
        new(
            CorrelationId: "stream-assembly-test",
            Model: "test-model",
            Messages: [new AiChatMessage(AiMessageRole.User, "hello")]);

    private sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(callback(request));
    }
}
