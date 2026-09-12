using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.ModelGateway.OpenAi;
using IncidentCompass.Infrastructure.OpenAiCompatible;
using IncidentCompass.TestSupport;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

public sealed class OpenAiCompatibleRetryPolicyTests
{
    private const string SuccessResponseJson = """
        {
          "model": "test-model",
          "choices": [{ "message": { "content": "ok" }, "finish_reason": "stop" }]
        }
        """;

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.RequestTimeout, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.NotImplemented, false)]
    [InlineData(HttpStatusCode.HttpVersionNotSupported, false)]
    public void IsRetryableGenerationStatus_AllowsOnlyExplicitAvailabilityStatuses(
        HttpStatusCode statusCode,
        bool expected)
    {
        Assert.Equal(
            expected,
            OpenAiCompatibleFailureClassifier.IsRetryableGenerationStatus(statusCode));
    }

    [Fact]
    public async Task CompleteAsync_DoesNotRetryConfiguredGenerationTimeout()
    {
        var attempts = 0;
        var handler = new CallbackHttpMessageHandler(async (_, cancellationToken) =>
        {
            attempts++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
        using var httpClient = new HttpClient(handler);
        var modelClient = CreateModelClient(httpClient, maxRetryAttempts: 3, timeoutSeconds: 1);

        var exception = await Assert.ThrowsAsync<AiModelException>(() =>
            modelClient.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(1, attempts);
        Assert.Equal(ProviderFailureKind.GenerationTimeout, exception.FailureKind);
        Assert.Equal("provider_generation_timeout", exception.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_ClassifiesDefensiveEndpointValidationAsRejectedRequest()
    {
        var attempts = 0;
        var handler = new CallbackHttpMessageHandler((_, _) =>
        {
            attempts++;
            return Task.FromResult(CreateResponse(HttpStatusCode.OK, SuccessResponseJson));
        });
        using var httpClient = new HttpClient(handler);
        var options = Options.Create(new OpenAiCompatibleModelClientOptions
        {
            BaseUrl = "not-a-valid-uri",
            ApiKey = "test-api-key"
        });
        var modelClient = new OpenAiCompatibleModelClient(httpClient, options, TestModelProviderProfiles.CreateResolver());

        var exception = await Assert.ThrowsAsync<AiModelException>(() =>
            modelClient.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(0, attempts);
        Assert.Equal("configuration_error", exception.ErrorCode);
        Assert.Equal(ProviderFailureKind.RejectedRequest, exception.FailureKind);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true, ProviderFailureKind.Unavailable, "provider_unavailable")]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, ProviderFailureKind.Unavailable, "provider_unavailable")]
    [InlineData(HttpStatusCode.RequestTimeout, false, ProviderFailureKind.GenerationTimeout, "provider_generation_timeout")]
    [InlineData(HttpStatusCode.NotImplemented, false, ProviderFailureKind.RejectedRequest, "provider_request_rejected")]
    [InlineData(HttpStatusCode.HttpVersionNotSupported, false, ProviderFailureKind.RejectedRequest, "provider_request_rejected")]
    [InlineData(HttpStatusCode.InternalServerError, false, ProviderFailureKind.AmbiguousInterruption, "provider_dispatch_outcome_unknown")]
    [InlineData(HttpStatusCode.BadGateway, false, ProviderFailureKind.AmbiguousInterruption, "provider_dispatch_outcome_unknown")]
    [InlineData(HttpStatusCode.GatewayTimeout, false, ProviderFailureKind.AmbiguousInterruption, "provider_dispatch_outcome_unknown")]
    public async Task CompleteAsync_RetriesOnlyExplicitAvailabilityStatuses(
        HttpStatusCode statusCode,
        bool shouldRetry,
        ProviderFailureKind expectedFailureKind,
        string expectedErrorCode)
    {
        var attempts = 0;
        var handler = new CallbackHttpMessageHandler((_, _) =>
        {
            attempts++;
            return Task.FromResult(attempts == 1
                ? CreateResponse(statusCode, "{\"error\":{\"code\":\"provider-code\"}}")
                : CreateResponse(HttpStatusCode.OK, SuccessResponseJson));
        });
        using var httpClient = new HttpClient(handler);
        var modelClient = CreateModelClient(httpClient, maxRetryAttempts: 1);

        if (shouldRetry)
        {
            var response = await modelClient.CompleteAsync(
                CreateRequest(),
                TestContext.Current.CancellationToken);

            Assert.Equal("ok", response.Content);
            Assert.Equal(2, attempts);
            return;
        }

        var exception = await Assert.ThrowsAsync<AiModelException>(() =>
            modelClient.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken));
        Assert.Equal(1, attempts);
        Assert.Equal(expectedFailureKind, exception.FailureKind);
        Assert.Equal(expectedErrorCode, exception.ErrorCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void ErrorMapper_ClassifiesExplicitAvailabilityStatus(HttpStatusCode statusCode)
    {
        var exception = OpenAiModelErrorMapper.FromHttpFailure(statusCode, "{}");

        Assert.Equal(ProviderFailureKind.Unavailable, exception.FailureKind);
        Assert.Equal("provider_unavailable", exception.ErrorCode);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, null, false, true)]
    [InlineData(HttpRequestError.SecureConnectionError, null, false, true)]
    [InlineData(HttpRequestError.ProxyTunnelError, null, false, true)]
    [InlineData(HttpRequestError.ConnectionError, SocketError.ConnectionRefused, false, true)]
    [InlineData(HttpRequestError.ConnectionError, SocketError.TimedOut, true, true)]
    [InlineData(HttpRequestError.ConnectionError, SocketError.HostUnreachable, false, true)]
    [InlineData(HttpRequestError.ConnectionError, SocketError.NetworkUnreachable, true, true)]
    [InlineData(HttpRequestError.ConnectionError, SocketError.HostNotFound, false, true)]
    [InlineData(HttpRequestError.ConnectionError, SocketError.AddressNotAvailable, true, true)]
    [InlineData(HttpRequestError.ConnectionError, SocketError.ConnectionReset, true, false)]
    [InlineData(HttpRequestError.ConnectionError, null, false, false)]
    [InlineData(HttpRequestError.ResponseEnded, null, false, false)]
    [InlineData(HttpRequestError.ResponseEnded, null, true, false)]
    [InlineData(HttpRequestError.ResponseEnded, SocketError.ConnectionRefused, true, false)]
    [InlineData(HttpRequestError.Unknown, null, false, false)]
    [InlineData(HttpRequestError.Unknown, SocketError.ConnectionRefused, true, false)]
    public async Task CompleteAsync_RetriesOnlySafePreDispatchTransportFailures(
        HttpRequestError requestError,
        SocketError? socketError,
        bool nestSocketException,
        bool shouldRetry)
    {
        var attempts = 0;
        var handler = new CallbackHttpMessageHandler((_, _) =>
        {
            attempts++;
            if (attempts == 1)
            {
                var innerException = CreateTransportInnerException(
                    requestError,
                    socketError,
                    nestSocketException);
                throw new HttpRequestException(requestError, "simulated transport failure", innerException, null);
            }

            return Task.FromResult(CreateResponse(HttpStatusCode.OK, SuccessResponseJson));
        });
        using var httpClient = new HttpClient(handler);
        var modelClient = CreateModelClient(httpClient, maxRetryAttempts: 1);

        if (shouldRetry)
        {
            var response = await modelClient.CompleteAsync(
                CreateRequest(),
                TestContext.Current.CancellationToken);

            Assert.Equal("ok", response.Content);
            Assert.Equal(2, attempts);
            return;
        }

        var exception = await Assert.ThrowsAsync<AiModelException>(() =>
            modelClient.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken));
        Assert.Equal(1, attempts);
        Assert.Equal(ProviderFailureKind.AmbiguousInterruption, exception.FailureKind);
        Assert.Equal("provider_dispatch_outcome_unknown", exception.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_PropagatesCallerCancellationWithoutRetry()
    {
        var attempts = 0;
        var handler = new CallbackHttpMessageHandler(async (_, cancellationToken) =>
        {
            attempts++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
        using var httpClient = new HttpClient(handler);
        var modelClient = CreateModelClient(httpClient, maxRetryAttempts: 3, timeoutSeconds: 30);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            modelClient.CompleteAsync(CreateRequest(), cancellation.Token));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CompleteAsync_NormalizesInvalidJsonWithoutRetry()
    {
        var attempts = 0;
        var handler = new CallbackHttpMessageHandler((_, _) =>
        {
            attempts++;
            return Task.FromResult(CreateResponse(HttpStatusCode.OK, "not-json"));
        });
        using var httpClient = new HttpClient(handler);
        var modelClient = CreateModelClient(httpClient, maxRetryAttempts: 3);

        var exception = await Assert.ThrowsAsync<AiModelException>(() =>
            modelClient.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(1, attempts);
        Assert.Equal(ProviderFailureKind.InvalidResponse, exception.FailureKind);
        Assert.Equal("invalid_json", exception.ErrorCode);
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(20, 5)]
    public void CalculateDelay_HonorsRetryAfterDeltaAndCeiling(
        int retryAfterSeconds,
        int expectedSeconds)
    {
        using var response = new HttpResponseMessage();
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));

        var delay = OpenAiCompatibleRetryPolicy.CalculateDelay(
            retryBaseDelayMilliseconds: 200,
            maxRetryDelaySeconds: 5,
            response,
            attempt: 0,
            now: DateTimeOffset.Parse("2026-09-08T12:00:00Z", CultureInfo.InvariantCulture));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(20, 5)]
    public void CalculateDelay_HonorsRetryAfterDateRelativeToProvidedNow(
        int retryAfterSeconds,
        int expectedSeconds)
    {
        var now = DateTimeOffset.Parse("2026-09-08T12:00:00Z", CultureInfo.InvariantCulture);
        using var response = new HttpResponseMessage();
        response.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(retryAfterSeconds));

        var delay = OpenAiCompatibleRetryPolicy.CalculateDelay(
            retryBaseDelayMilliseconds: 200,
            maxRetryDelaySeconds: 5,
            response,
            attempt: 4,
            now);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void CalculateDelay_UsesFallbackForPastRetryAfterDate()
    {
        var now = DateTimeOffset.Parse("2026-09-08T12:00:00Z", CultureInfo.InvariantCulture);
        using var response = new HttpResponseMessage();
        response.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(-1));

        var delay = OpenAiCompatibleRetryPolicy.CalculateDelay(
            retryBaseDelayMilliseconds: 200,
            maxRetryDelaySeconds: 5,
            response,
            attempt: 2,
            now);

        Assert.Equal(TimeSpan.FromMilliseconds(800), delay);
    }

    [Fact]
    public void CalculateDelay_UsesFallbackForZeroRetryAfterDelta()
    {
        using var response = new HttpResponseMessage();
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);

        var delay = OpenAiCompatibleRetryPolicy.CalculateDelay(
            retryBaseDelayMilliseconds: 200,
            maxRetryDelaySeconds: 5,
            response,
            attempt: 2,
            now: DateTimeOffset.Parse("2026-09-08T12:00:00Z", CultureInfo.InvariantCulture));

        Assert.Equal(TimeSpan.FromMilliseconds(800), delay);
    }

    [Fact]
    public void CalculateDelay_ClampsExponentialFallbackToConfiguredCeiling()
    {
        var delay = OpenAiCompatibleRetryPolicy.CalculateDelay(
            retryBaseDelayMilliseconds: 200,
            maxRetryDelaySeconds: 5,
            response: null,
            attempt: 5,
            now: DateTimeOffset.Parse("2026-09-08T12:00:00Z", CultureInfo.InvariantCulture));

        Assert.Equal(TimeSpan.FromSeconds(5), delay);
    }

    [Fact]
    public async Task DelayBeforeRetryAsync_ObservesCancellation()
    {
        var retryPolicy = new OpenAiCompatibleRetryPolicy();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            retryPolicy.DelayBeforeRetryAsync(
                retryBaseDelayMilliseconds: 1000,
                maxRetryDelaySeconds: 5,
                response: null,
                attempt: 0,
                cancellation.Token));
    }

    private static OpenAiCompatibleModelClient CreateModelClient(
        HttpClient httpClient,
        int maxRetryAttempts,
        int timeoutSeconds = 30)
    {
        var options = Options.Create(new OpenAiCompatibleModelClientOptions
        {
            BaseUrl = "https://provider.example",
            ApiKey = "test-api-key",
            MaxRetryAttempts = maxRetryAttempts,
            RetryBaseDelayMilliseconds = 1,
            MaxRetryDelaySeconds = 5,
            TimeoutSeconds = timeoutSeconds
        });
        return new OpenAiCompatibleModelClient(httpClient, options, TestModelProviderProfiles.CreateResolver());
    }

    private static AiModelRequest CreateRequest()
    {
        return new AiModelRequest(
            CorrelationId: "provider-protocol-test",
            Model: "test-model",
            Messages: [new AiChatMessage(AiMessageRole.User, "hello")]);
    }

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string responseContent)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseContent)
        };
    }

    private static Exception? CreateTransportInnerException(
        HttpRequestError requestError,
        SocketError? socketError,
        bool nestSocketException)
    {
        if (socketError is null)
        {
            return requestError == HttpRequestError.ResponseEnded && nestSocketException
                ? new InvalidOperationException(
                    "Nested response-ended failure.",
                    new EndOfStreamException("Simulated EOF."))
                : null;
        }

        var socketException = new SocketException((int)socketError.Value);
        return nestSocketException
            ? new InvalidOperationException("Nested transport failure.", socketException)
            : socketException;
    }

    private sealed class CallbackHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return callback(request, cancellationToken);
        }
    }
}
