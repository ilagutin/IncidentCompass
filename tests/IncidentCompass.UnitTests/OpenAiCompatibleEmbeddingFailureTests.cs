using System.Net;
using System.Net.Sockets;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.Resilience;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.Embeddings.OpenAi;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

public sealed class OpenAiCompatibleEmbeddingFailureTests
{
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.NotImplemented, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.HttpVersionNotSupported, true)]
    public void ShouldRetry_PreservesIdempotentEmbeddingStatusSet(
        HttpStatusCode statusCode,
        bool expected)
    {
        var retryPolicy = new OpenAiEmbeddingRetryPolicy();

        Assert.Equal(expected, retryPolicy.ShouldRetry(statusCode));
    }

    [Theory]
    [InlineData(ProviderFailureKind.Unavailable, true)]
    [InlineData(ProviderFailureKind.RejectedRequest, false)]
    [InlineData(ProviderFailureKind.GenerationTimeout, false)]
    [InlineData(ProviderFailureKind.OutputLimitReached, false)]
    [InlineData(ProviderFailureKind.AmbiguousInterruption, false)]
    [InlineData(ProviderFailureKind.TransportFailure, false)]
    [InlineData(ProviderFailureKind.InvalidResponse, false)]
    [InlineData(ProviderFailureKind.Unknown, false)]
    public void ProviderOutageClassifier_UsesEmbeddingFailureKindAcrossNestedChains(
        ProviderFailureKind failureKind,
        bool expected)
    {
        var embeddingFailure = new EmbeddingClientException(
            "test-provider",
            "Embedding provider failure.",
            failureKind: failureKind);

        Assert.Equal(expected, ProviderOutageExceptionClassifier.IsProviderOutage(embeddingFailure));
        Assert.Equal(
            expected,
            ProviderOutageExceptionClassifier.IsProviderOutage(
                new InvalidOperationException("Outer wrapper.", embeddingFailure)));
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, null, false, ProviderFailureKind.Unavailable, true)]
    [InlineData(HttpRequestError.SecureConnectionError, null, false, ProviderFailureKind.Unavailable, true)]
    [InlineData(HttpRequestError.ProxyTunnelError, null, false, ProviderFailureKind.Unavailable, true)]
    [InlineData(HttpRequestError.ConnectionError, SocketError.ConnectionRefused, false, ProviderFailureKind.Unavailable, true)]
    [InlineData(HttpRequestError.ConnectionError, SocketError.TimedOut, true, ProviderFailureKind.Unavailable, true)]
    [InlineData(HttpRequestError.ConnectionError, SocketError.HostUnreachable, false, ProviderFailureKind.Unavailable, true)]
    [InlineData(HttpRequestError.ConnectionError, SocketError.NetworkUnreachable, true, ProviderFailureKind.Unavailable, true)]
    [InlineData(HttpRequestError.ConnectionError, SocketError.HostNotFound, false, ProviderFailureKind.Unavailable, true)]
    [InlineData(HttpRequestError.ConnectionError, SocketError.AddressNotAvailable, true, ProviderFailureKind.Unavailable, true)]
    [InlineData(HttpRequestError.ConnectionError, SocketError.ConnectionReset, true, ProviderFailureKind.TransportFailure, false)]
    [InlineData(HttpRequestError.ConnectionError, null, false, ProviderFailureKind.TransportFailure, false)]
    [InlineData(HttpRequestError.ResponseEnded, null, false, ProviderFailureKind.TransportFailure, false)]
    [InlineData(HttpRequestError.ResponseEnded, null, true, ProviderFailureKind.TransportFailure, false)]
    [InlineData(HttpRequestError.ResponseEnded, SocketError.ConnectionRefused, true, ProviderFailureKind.TransportFailure, false)]
    [InlineData(HttpRequestError.Unknown, null, false, ProviderFailureKind.TransportFailure, false)]
    [InlineData(HttpRequestError.Unknown, SocketError.ConnectionRefused, true, ProviderFailureKind.TransportFailure, false)]
    public void Transport_SeparatesConfirmedAvailabilityFromTransportFailure(
        HttpRequestError requestError,
        SocketError? socketError,
        bool nestSocketException,
        ProviderFailureKind expectedFailureKind,
        bool expectedOutage)
    {
        var innerException = CreateTransportInnerException(
            requestError,
            socketError,
            nestSocketException);
        var transportFailure = new HttpRequestException(
            requestError,
            "simulated transport failure",
            innerException,
            statusCode: null);
        var embeddingFailure = new OpenAiEmbeddingErrorMapper().Transport(transportFailure);

        Assert.Equal(expectedFailureKind, embeddingFailure.FailureKind);
        Assert.Equal(
            expectedOutage ? "provider_unavailable" : "transport_error",
            embeddingFailure.ErrorCode);
        Assert.Equal(
            expectedOutage,
            ProviderOutageExceptionClassifier.IsProviderOutage(
                new InvalidOperationException("Outer wrapper.", embeddingFailure)));
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, null, ProviderFailureKind.Unavailable, "provider_unavailable", true)]
    [InlineData(HttpRequestError.ConnectionError, SocketError.ConnectionRefused, ProviderFailureKind.Unavailable, "provider_unavailable", true)]
    [InlineData(HttpRequestError.ConnectionError, SocketError.ConnectionReset, ProviderFailureKind.TransportFailure, "transport_error", false)]
    [InlineData(HttpRequestError.ResponseEnded, null, ProviderFailureKind.TransportFailure, "transport_error", false)]
    public async Task CreateEmbeddingAsync_ExhaustsBoundedTransportRetries(
        HttpRequestError requestError,
        SocketError? socketError,
        ProviderFailureKind expectedFailureKind,
        string expectedErrorCode,
        bool expectedOutage)
    {
        var attempts = 0;
        var handler = new CallbackHttpMessageHandler((_, _) =>
        {
            attempts++;
            var innerException = CreateTransportInnerException(
                requestError,
                socketError,
                nestSocketException: true);
            throw new HttpRequestException(
                requestError,
                "simulated embedding transport failure",
                innerException,
                statusCode: null);
        });
        using var httpClient = new HttpClient(handler);
        var options = Options.Create(new OpenAiCompatibleEmbeddingClientOptions
        {
            BaseUrl = "https://provider.example",
            ApiKey = "test-api-key",
            MaxRetryAttempts = 2,
            RetryBaseDelayMilliseconds = 1,
            TimeoutSeconds = 30
        });
        var client = new OpenAiCompatibleEmbeddingClient(httpClient, options);

        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            client.CreateEmbeddingAsync(
                new EmbeddingRequest("hello", "embedding-model", "embedding-transport-test"),
                TestContext.Current.CancellationToken));

        Assert.Equal(3, attempts);
        Assert.Equal(expectedFailureKind, exception.FailureKind);
        Assert.Equal(expectedErrorCode, exception.ErrorCode);
        Assert.Equal(expectedOutage, ProviderOutageExceptionClassifier.IsProviderOutage(exception));
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
