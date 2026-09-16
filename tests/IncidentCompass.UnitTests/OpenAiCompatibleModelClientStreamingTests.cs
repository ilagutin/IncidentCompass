using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.ModelGateway.OpenAi;
using IncidentCompass.TestSupport;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

/// <summary>
/// On a streamed answer the provider call limits watch model output, not the connection: the first
/// data event ends the first-output phase, every later data event restarts the inactivity limit, and
/// keep-alive comments restart nothing. Every limit is driven by a manual clock, and the clock is only
/// advanced once the client has consumed everything written and is blocked on its next read.
/// </summary>
public sealed class OpenAiCompatibleModelClientStreamingTests
{
    private const string FirstChunk = "data: {\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"a\"}}]}\n\n";
    private const string KeepAlive = ": keep-alive\n\n";

    [Fact]
    public async Task CompleteAsync_StreamThatStallsAfterItsFirstEvent_EndsWithInactivityTimeoutDespiteKeepAlives()
    {
        var time = new ManualTimerTimeProvider();
        var body = new ControlledResponseStream();
        var attempts = 0;
        var client = CreateClient(
            new CallbackHandler((_, _) =>
            {
                Interlocked.Increment(ref attempts);
                return Task.FromResult(EventStreamResponse(body));
            }),
            time,
            StreamingOptions());

        var call = client.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);
        await WriteAndWaitAsync(body, FirstChunk);
        for (var keepAlive = 0; keepAlive < 9; keepAlive++)
        {
            time.Advance(TimeSpan.FromSeconds(60));
            await WriteAndWaitAsync(body, KeepAlive);
        }

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(call.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<AiModelException>(() => call);
        Assert.Equal(ProviderFailureKind.GenerationTimeout, exception.FailureKind);
        Assert.Equal(ProviderErrorCodes.StreamInactivityTimeout, exception.ErrorCode);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CompleteAsync_StreamWithHeadersButNoDataEvent_EndsWithFirstOutputTimeoutMeasuredFromDispatch()
    {
        var time = new ManualTimerTimeProvider();
        var attempts = 0;
        var body = new ControlledResponseStream();
        var handler = new CallbackHandler((_, _) =>
        {
            Interlocked.Increment(ref attempts);

            // Headers arrive 100 seconds after dispatch; the limit still counts from dispatch.
            time.Advance(TimeSpan.FromSeconds(100));
            return Task.FromResult(EventStreamResponse(body));
        });
        var client = CreateClient(handler, time, StreamingOptions());

        var call = client.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);
        await body.WaitForIdleReadAsync().WaitAsync(TestContext.Current.CancellationToken);
        for (var keepAlive = 0; keepAlive < 8; keepAlive++)
        {
            time.Advance(TimeSpan.FromSeconds(60));
            await WriteAndWaitAsync(body, KeepAlive);
        }

        time.Advance(TimeSpan.FromSeconds(19));
        Assert.False(call.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<AiModelException>(() => call);
        Assert.Equal(ProviderFailureKind.GenerationTimeout, exception.FailureKind);
        Assert.Equal(ProviderErrorCodes.FirstOutputTimeout, exception.ErrorCode);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CompleteAsync_SlowButLiveStream_CompletesWithAssembledContentAndUsage()
    {
        var time = new ManualTimerTimeProvider();
        var body = new ControlledResponseStream();
        var client = CreateClient(StreamingHandler(body), time, StreamingOptions());

        var call = client.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);
        await body.WaitForIdleReadAsync().WaitAsync(TestContext.Current.CancellationToken);
        for (var piece = 0; piece < 6; piece++)
        {
            time.Advance(TimeSpan.FromSeconds(500));
            Assert.False(call.IsCompleted);
            await WriteAndWaitAsync(body, ContentEvent(piece.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        body.Write("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
        body.Write("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":7,\"completion_tokens\":6,\"total_tokens\":13}}\n\n");
        body.Write("data: [DONE]\n\n");

        var response = await call.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("012345", response.Content);
        Assert.Equal("test-model", response.Model);
        Assert.NotNull(response.Usage);
        Assert.Equal(7, response.Usage.InputTokens);
        Assert.Equal(6, response.Usage.OutputTokens);
        Assert.Equal(13, response.Usage.TotalTokens);
    }

    [Fact]
    public async Task CompleteAsync_HostCancellationMidStream_PropagatesAsCancellation()
    {
        var body = new ControlledResponseStream();
        var client = CreateClient(StreamingHandler(body), new ManualTimerTimeProvider(), StreamingOptions());
        using var host = new CancellationTokenSource();

        var call = client.CompleteAsync(CreateRequest(), host.Token);
        await WriteAndWaitAsync(body, FirstChunk);
        await host.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    [Fact]
    public async Task CompleteAsync_StreamLargerThanTheClientBufferLimit_IsRefusedAsAnInvalidResponse()
    {
        var body = new ControlledResponseStream();
        body.Write(FirstChunk);
        body.Write(": " + new string('x', 64) + "\n\n");
        body.Complete();
        using var httpClient = new HttpClient(StreamingHandler(body)) { MaxResponseContentBufferSize = FirstChunk.Length + 16 };
        httpClient.Timeout = Timeout.InfiniteTimeSpan;
        var client = new OpenAiCompatibleModelClient(
            httpClient,
            Options.Create(StreamingOptions()),
            TestModelProviderProfiles.CreateResolver(),
            new ManualTimerTimeProvider());

        var exception = await Assert.ThrowsAsync<AiModelException>(() =>
            client.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(ProviderFailureKind.InvalidResponse, exception.FailureKind);
        Assert.Equal(OpenAiModelErrorMapper.ResponseTooLargeCode, exception.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_ProviderThatIgnoresStreamAndAnswersJson_IsReadAsBefore()
    {
        string? sentPayload = null;
        var handler = new CallbackHandler(async (request, cancellationToken) =>
        {
            sentPayload = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"model\":\"test-model\",\"choices\":[{\"message\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var client = CreateClient(handler, new ManualTimerTimeProvider(), StreamingOptions());

        var response = await client.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        Assert.Equal("ok", response.Content);
        using var payload = JsonDocument.Parse(sentPayload!);
        Assert.True(payload.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public async Task CompleteAsync_StreamingSwitchedOff_SendsNoStreamFields()
    {
        string? sentPayload = null;
        var handler = new CallbackHandler(async (request, cancellationToken) =>
        {
            sentPayload = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var options = new OpenAiCompatibleModelClientOptions
        {
            BaseUrl = "https://provider.example",
            ApiKey = "test-api-key",
            Streaming = false
        };
        var client = CreateClient(handler, new ManualTimerTimeProvider(), options);

        await client.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        using var payload = JsonDocument.Parse(sentPayload!);
        Assert.False(payload.RootElement.TryGetProperty("stream", out _));
        Assert.False(payload.RootElement.TryGetProperty("stream_options", out _));
    }

    [Fact]
    public void Options_StreamingIsOnByDefault()
    {
        Assert.True(new OpenAiCompatibleModelClientOptions().Streaming);
    }

    /// <summary>
    /// A failure status is read and mapped as it always was, even when the body is labelled as an
    /// event stream.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_FailureStatusWithAnEventStreamLabel_IsMappedAsAnHttpFailure()
    {
        var handler = new CallbackHandler((_, _) =>
        {
            var content = new StringContent("{\"error\":{\"message\":\"no\",\"code\":\"bad_thing\"}}");
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = content });
        });
        var client = CreateClient(handler, new ManualTimerTimeProvider(), StreamingOptions());

        var exception = await Assert.ThrowsAsync<AiModelException>(() =>
            client.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(ProviderFailureKind.RejectedRequest, exception.FailureKind);
        Assert.Equal("bad_thing", exception.ProviderErrorCode);
    }

    /// <summary>
    /// A retryable status is retried as on the JSON path whatever its content type says, because the
    /// status, not the label, is what says nothing was generated.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task CompleteAsync_RetryableStatusWithAnEventStreamLabel_IsRetried(HttpStatusCode status)
    {
        var attempts = 0;
        var handler = new CallbackHandler((_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                var busy = new StringContent("{\"error\":{\"code\":\"busy\"}}");
                busy.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return Task.FromResult(new HttpResponseMessage(status) { Content = busy });
            }

            var answer = new StringContent(
                FirstChunk + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
            answer.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = answer });
        });
        var client = CreateClient(handler, new ManualTimerTimeProvider(), StreamingOptions());

        var response = await client.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(2, attempts);
        Assert.Equal("a", response.Content);
    }

    /// <summary>
    /// One line longer than the parser's per-event cap is refused as too large while it is still being
    /// buffered, well inside the registered 32 MiB body limit, and never reaches JSON parsing.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_SingleLineLongerThanTheEventCap_IsRefusedAsTooLarge()
    {
        var body = new ControlledResponseStream();
        body.Write("data: " + new string('x', OpenAiServerSentEventParser.DefaultMaxEventCharacters));
        body.Complete();
        using var httpClient = new HttpClient(StreamingHandler(body))
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = OpenAiCompatibleModelClient.MaxResponseContentBytes
        };
        var client = new OpenAiCompatibleModelClient(
            httpClient,
            Options.Create(StreamingOptions()),
            TestModelProviderProfiles.CreateResolver(),
            new ManualTimerTimeProvider());

        var exception = await Assert.ThrowsAsync<AiModelException>(() =>
            client.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(OpenAiModelErrorMapper.ResponseTooLargeCode, exception.ErrorCode);
        Assert.IsType<OpenAiResponseBodyTooLargeException>(exception.InnerException);
    }

    private static async Task WriteAndWaitAsync(ControlledResponseStream body, string text)
    {
        body.Write(text);
        await body.WaitForIdleReadAsync().WaitAsync(TestContext.Current.CancellationToken);
    }

    private static string ContentEvent(string text) =>
        "data: {\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" + text + "\"}}]}\n\n";

    private static CallbackHandler StreamingHandler(ControlledResponseStream body) =>
        new((_, _) => Task.FromResult(EventStreamResponse(body)));

    private static HttpResponseMessage EventStreamResponse(Stream body)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static OpenAiCompatibleModelClient CreateClient(
        HttpMessageHandler handler,
        TimeProvider timeProvider,
        OpenAiCompatibleModelClientOptions options)
    {
        // As registered: the typed client carries no deadline of its own.
        var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return new(
            httpClient,
            Options.Create(options),
            TestModelProviderProfiles.CreateResolver(),
            timeProvider);
    }

    private static OpenAiCompatibleModelClientOptions StreamingOptions() =>
        new()
        {
            BaseUrl = "https://provider.example",
            ApiKey = "test-api-key",
            MaxRetryAttempts = 3,
            RetryBaseDelayMilliseconds = 1
        };

    private static AiModelRequest CreateRequest() =>
        new(
            CorrelationId: "streaming-test",
            Model: "test-model",
            Messages: [new AiChatMessage(AiMessageRole.User, "hello")]);

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            callback(request, cancellationToken);
    }
}
