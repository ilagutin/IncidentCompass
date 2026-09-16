using System.Net;
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
/// The provider call limits name the phase that stalled: connecting, waiting for the response to
/// start, or a response body that stopped arriving. Every limit here is driven by a manual clock and
/// a handler that waits on its cancellation token, so no test spends or races real time. Each ends a
/// call with the generation-timeout failure kind and no in-client retry, as the single timeout did.
/// </summary>
public sealed class OpenAiCompatibleModelClientCallLimitTests
{
    private const string SuccessResponseJson = """
        {
          "model": "test-model",
          "choices": [{ "message": { "content": "ok" }, "finish_reason": "stop" }]
        }
        """;

    [Fact]
    public async Task CompleteAsync_ResponseThatNeverStarts_EndsWithFirstOutputTimeoutAtTheLimit()
    {
        var time = new ManualTimerTimeProvider();
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        using var httpClient = new HttpClient(new CallbackHandler(async (_, cancellationToken) =>
        {
            attempts++;
            dispatched.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }));
        var client = CreateClient(httpClient, time, new OpenAiCompatibleModelClientOptions
        {
            BaseUrl = "https://provider.example",
            ApiKey = "test-api-key",
            MaxRetryAttempts = 3,
            RetryBaseDelayMilliseconds = 1
        });

        var call = client.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);
        await dispatched.Task.WaitAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(599));
        Assert.False(call.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<AiModelException>(() => call);
        Assert.Equal(1, attempts);
        Assert.Equal(ProviderFailureKind.GenerationTimeout, exception.FailureKind);
        Assert.Equal(ProviderErrorCodes.FirstOutputTimeout, exception.ErrorCode);
        Assert.Equal(ProviderErrorCodes.FirstOutputTimeout, ProviderErrorCodes.For(exception.FailureKind, exception));
    }

    [Fact]
    public async Task CompleteAsync_DeprecatedTimeoutSeconds_IsTheFirstOutputLimit()
    {
        var time = new ManualTimerTimeProvider();
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = new HttpClient(new CallbackHandler(async (_, cancellationToken) =>
        {
            dispatched.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }));
        var client = CreateClient(httpClient, time, new OpenAiCompatibleModelClientOptions
        {
            BaseUrl = "https://provider.example",
            ApiKey = "test-api-key",
            TimeoutSeconds = 420
        });

        var call = client.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);
        await dispatched.Task.WaitAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(419));
        Assert.False(call.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<AiModelException>(() => call);
        Assert.Equal(ProviderErrorCodes.FirstOutputTimeout, exception.ErrorCode);
    }

    /// <summary>
    /// The connect limit is enforced by the socket handler, which reports it as a cancellation
    /// carrying a <see cref="TimeoutException" />. The handler here raises exactly that shape, so the
    /// client's classification is exercised without opening a socket.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_ConnectPhaseTimeout_EndsWithConnectTimeoutAndIsNotRetried()
    {
        var attempts = 0;
        using var httpClient = new HttpClient(new CallbackHandler((_, _) =>
        {
            attempts++;
            throw new TaskCanceledException(
                "A connection could not be established within the configured ConnectTimeout.",
                new TimeoutException("A connection could not be established within the configured ConnectTimeout."));
        }));
        var client = CreateClient(httpClient, new ManualTimerTimeProvider(), new OpenAiCompatibleModelClientOptions
        {
            BaseUrl = "https://provider.example",
            ApiKey = "test-api-key",
            MaxRetryAttempts = 3,
            RetryBaseDelayMilliseconds = 1
        });

        var exception = await Assert.ThrowsAsync<AiModelException>(() =>
            client.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(1, attempts);
        Assert.Equal(ProviderFailureKind.GenerationTimeout, exception.FailureKind);
        Assert.Equal(ProviderErrorCodes.ConnectTimeout, exception.ErrorCode);
        Assert.Equal(ProviderErrorCodes.ConnectTimeout, ProviderErrorCodes.For(exception.FailureKind, exception));
    }

    [Fact]
    public async Task CompleteAsync_BodyThatStopsArriving_EndsWithInactivityTimeoutMeasuredFromTheLastBytes()
    {
        var time = new ManualTimerTimeProvider();
        var body = new StallingBodyStream(Encoding.UTF8.GetBytes("{\"model\":"));
        using var httpClient = new HttpClient(new CallbackHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) })));
        var client = CreateClient(httpClient, time, new OpenAiCompatibleModelClientOptions
        {
            BaseUrl = "https://provider.example",
            ApiKey = "test-api-key"
        });

        var call = client.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);
        await body.FirstReadWaiting.Task.WaitAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(500));
        body.ReleaseFirstRead.TrySetResult();
        await body.SecondReadWaiting.Task.WaitAsync(TestContext.Current.CancellationToken);

        // 1000 seconds since the body started, but only 500 since it last delivered bytes.
        time.Advance(TimeSpan.FromSeconds(500));
        Assert.False(call.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(100));

        var exception = await Assert.ThrowsAsync<AiModelException>(() => call);
        Assert.Equal(ProviderFailureKind.GenerationTimeout, exception.FailureKind);
        Assert.Equal(ProviderErrorCodes.StreamInactivityTimeout, exception.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_HostCancellationWhileWaitingForOutput_PropagatesAsCancellation()
    {
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = new HttpClient(new CallbackHandler(async (_, cancellationToken) =>
        {
            dispatched.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }));
        var client = CreateClient(httpClient, new ManualTimerTimeProvider(), new OpenAiCompatibleModelClientOptions
        {
            BaseUrl = "https://provider.example",
            ApiKey = "test-api-key"
        });
        using var host = new CancellationTokenSource();

        var call = client.CompleteAsync(CreateRequest(), host.Token);
        await dispatched.Task.WaitAsync(TestContext.Current.CancellationToken);
        await host.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    /// <summary>
    /// A cancellation from below the client that is neither the caller's, a timer of the adapter's,
    /// nor shaped like the handler's connect limit is not claimed as a connect timeout.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_UnattributedCancellationBeforeTheResponse_IsADispatchWithUnknownOutcome()
    {
        var attempts = 0;
        using var httpClient = new HttpClient(new CallbackHandler((_, _) =>
        {
            attempts++;
            throw new OperationCanceledException("Cancelled by something inside the handler.");
        }));
        var client = CreateClient(httpClient, new ManualTimerTimeProvider(), RetryingOptions());

        var exception = await Assert.ThrowsAsync<AiModelException>(() =>
            client.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(1, attempts);
        Assert.Equal(ProviderFailureKind.AmbiguousInterruption, exception.FailureKind);
        Assert.Equal("provider_dispatch_outcome_unknown", exception.ErrorCode);
    }

    /// <summary>
    /// The connect-limit shape is also what an HTTP client's own timeout produces. The registered
    /// client has none, but a client that does cannot have its shape attributed to the connect phase.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_ConnectShapedCancellationOnAClientWithItsOwnTimeout_IsNotClaimedAsConnect()
    {
        using var httpClient = new HttpClient(new CallbackHandler((_, _) =>
            throw new TaskCanceledException("Timed out.", new TimeoutException("Timed out."))));
        var client = CreateClient(httpClient, new ManualTimerTimeProvider(), RetryingOptions());
        httpClient.Timeout = TimeSpan.FromMinutes(30);

        var exception = await Assert.ThrowsAsync<AiModelException>(() =>
            client.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(ProviderFailureKind.AmbiguousInterruption, exception.FailureKind);
        Assert.Equal("provider_dispatch_outcome_unknown", exception.ErrorCode);
    }

    public static TheoryData<string> MidBodyFailures => new() { "io", "http" };

    /// <summary>
    /// The response started, so the request was sent. A body that breaks off mid-read is a dispatch
    /// whose outcome is unknown, mapped like any other failure after dispatch and never replayed.
    /// </summary>
    [Theory]
    [MemberData(nameof(MidBodyFailures))]
    public async Task CompleteAsync_BodyThatFailsMidRead_IsADispatchWithUnknownOutcomeAndIsNotRetried(string failure)
    {
        var attempts = 0;
        using var httpClient = new HttpClient(new CallbackHandler((_, _) =>
        {
            attempts++;
            Exception thrown = failure == "io"
                ? new IOException("The response ended prematurely.")
                : new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FailingBodyStream(Encoding.UTF8.GetBytes("{\"model\":"), thrown))
            });
        }));
        var client = CreateClient(httpClient, new ManualTimerTimeProvider(), RetryingOptions());

        var exception = await Assert.ThrowsAsync<AiModelException>(() =>
            client.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(1, attempts);
        Assert.Equal(ProviderFailureKind.AmbiguousInterruption, exception.FailureKind);
        Assert.Equal("provider_dispatch_outcome_unknown", exception.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_BodyLargerThanTheClientBufferLimit_IsRefusedAsAnInvalidResponse()
    {
        using var httpClient = new HttpClient(new CallbackHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SuccessResponseJson, Encoding.UTF8, "application/json")
            })))
        {
            MaxResponseContentBufferSize = 16
        };
        var client = CreateClient(httpClient, new ManualTimerTimeProvider(), RetryingOptions());

        var exception = await Assert.ThrowsAsync<AiModelException>(() =>
            client.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(ProviderFailureKind.InvalidResponse, exception.FailureKind);
        Assert.Equal(OpenAiModelErrorMapper.ResponseTooLargeCode, exception.ErrorCode);
        Assert.Equal(OpenAiModelErrorMapper.ResponseTooLargeCode, ProviderErrorCodes.For(exception.FailureKind, exception));
    }

    /// <summary>
    /// A retryable status spends most of the first attempt's first-output allowance; the retry must
    /// start with a whole allowance of its own rather than inherit what was left.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_RetryAfterA429_GetsAFreshFirstOutputLimit()
    {
        var time = new ManualTimerTimeProvider();
        var secondDispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        using var httpClient = new HttpClient(new CallbackHandler(async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                time.Advance(TimeSpan.FromSeconds(590));
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
            }

            secondDispatched.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }));
        var client = CreateClient(httpClient, time, RetryingOptions());

        var call = client.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);
        await secondDispatched.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Past the first attempt's deadline, well inside the retry's own.
        time.Advance(TimeSpan.FromSeconds(20));
        Assert.False(call.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(580));

        var exception = await Assert.ThrowsAsync<AiModelException>(() => call);
        Assert.Equal(2, attempts);
        Assert.Equal(ProviderErrorCodes.FirstOutputTimeout, exception.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_ResponseWithinLimits_ReturnsTheMappedContent()
    {
        using var httpClient = new HttpClient(new CallbackHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SuccessResponseJson, Encoding.UTF8, "application/json")
            })));
        var client = CreateClient(httpClient, new ManualTimerTimeProvider(), new OpenAiCompatibleModelClientOptions
        {
            BaseUrl = "https://provider.example",
            ApiKey = "test-api-key"
        });

        var response = await client.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        Assert.Equal("ok", response.Content);
    }

    private static OpenAiCompatibleModelClient CreateClient(
        HttpClient httpClient,
        TimeProvider timeProvider,
        OpenAiCompatibleModelClientOptions options)
    {
        // As registered: the typed client carries no deadline of its own.
        httpClient.Timeout = Timeout.InfiniteTimeSpan;
        return new(httpClient, Options.Create(options), TestModelProviderProfiles.CreateResolver(), timeProvider);
    }

    private static OpenAiCompatibleModelClientOptions RetryingOptions() =>
        new()
        {
            BaseUrl = "https://provider.example",
            ApiKey = "test-api-key",
            MaxRetryAttempts = 3,
            RetryBaseDelayMilliseconds = 1
        };

    private static AiModelRequest CreateRequest() =>
        new(
            CorrelationId: "call-limit-test",
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

    /// <summary>
    /// A body that hands out its first bytes only when the test releases them, then never delivers
    /// another byte and waits on the read's cancellation token.
    /// </summary>
    /// <summary>A body that delivers its first bytes and then fails the next read.</summary>
    private sealed class FailingBodyStream(byte[] firstChunk, Exception failure) : Stream
    {
        private int reads;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                firstChunk.CopyTo(buffer);
                return ValueTask.FromResult(firstChunk.Length);
            }

            return ValueTask.FromException<int>(failure);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StallingBodyStream(byte[] firstChunk) : Stream
    {
        private int reads;

        public TaskCompletionSource FirstReadWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondReadWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                FirstReadWaiting.TrySetResult();
                await ReleaseFirstRead.Task.WaitAsync(cancellationToken);
                firstChunk.CopyTo(buffer);
                return firstChunk.Length;
            }

            SecondReadWaiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
