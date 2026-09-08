using System.Net;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.Resilience;
using IncidentCompass.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IncidentCompass.IntegrationTests;

public sealed class OpenAiCompatibleEmbeddingClientTests
{
    private static readonly float[] FakeEmbeddingVector = [0.1f, 0.2f, 0.3f];

    [Fact]
    public async Task CreateEmbeddingAsync_RetriesTransientProviderStatus()
    {
        await using var app = CreateFakeOpenAiCompatibleServer();
        await app.StartAsync();
        var baseUrl = GetServerAddress(app);
        using var provider = CreateEmbeddingServiceProvider(
            baseUrl,
            new Dictionary<string, string?>
            {
                ["IncidentCompass:Embeddings:OpenAiCompatible:MaxRetryAttempts"] = "1"
            });
        var embeddingClient = provider.GetRequiredService<IEmbeddingClient>();

        var response = await embeddingClient.CreateEmbeddingAsync(
            new EmbeddingRequest("hello", "embedding-model", "embedding-retry-test"),
            TestContext.Current.CancellationToken);

        Assert.Equal("embedding-model", response.Model);
        Assert.Equal("openai-compatible", response.Provider);
        Assert.Equal([0.1f, 0.2f, 0.3f], response.Vector);
        Assert.Equal(1, response.InputTokens);
        Assert.Equal(2, app.Services.GetRequiredService<AttemptCounter>().Value);
    }

    [Theory]
    [InlineData(StatusCodes.Status408RequestTimeout)]
    [InlineData(StatusCodes.Status429TooManyRequests)]
    [InlineData(StatusCodes.Status500InternalServerError)]
    [InlineData(StatusCodes.Status501NotImplemented)]
    [InlineData(StatusCodes.Status502BadGateway)]
    [InlineData(StatusCodes.Status503ServiceUnavailable)]
    [InlineData(StatusCodes.Status504GatewayTimeout)]
    [InlineData(StatusCodes.Status505HttpVersionNotsupported)]
    public async Task CreateEmbeddingAsync_RetriesIdempotentLegacyStatusSet(int statusCode)
    {
        await using var app = CreateFakeOpenAiCompatibleServer(async context =>
        {
            var attempts = context.RequestServices.GetRequiredService<AttemptCounter>();
            attempts.Value++;
            if (attempts.Value == 1)
            {
                context.Response.StatusCode = statusCode;
                await context.Response.WriteAsJsonAsync(new { error = new { code = "provider-code" } });
                return;
            }

            await WriteSuccessfulEmbeddingAsync(context);
        });
        await app.StartAsync();
        using var provider = CreateEmbeddingServiceProvider(
            GetServerAddress(app),
            new Dictionary<string, string?>
            {
                ["IncidentCompass:Embeddings:OpenAiCompatible:MaxRetryAttempts"] = "1"
            });
        var embeddingClient = provider.GetRequiredService<IEmbeddingClient>();

        var response = await embeddingClient.CreateEmbeddingAsync(
            new EmbeddingRequest("hello", "embedding-model", "embedding-status-retry-test"),
            TestContext.Current.CancellationToken);

        Assert.Equal(FakeEmbeddingVector, response.Vector);
        Assert.Equal(2, app.Services.GetRequiredService<AttemptCounter>().Value);
    }

    [Theory]
    [InlineData((int)HttpStatusCode.BadRequest, "invalid_request", ProviderFailureKind.RejectedRequest)]
    [InlineData((int)HttpStatusCode.Unauthorized, "authentication_error", ProviderFailureKind.RejectedRequest)]
    [InlineData((int)HttpStatusCode.Forbidden, "authentication_error", ProviderFailureKind.RejectedRequest)]
    [InlineData((int)HttpStatusCode.RequestTimeout, "provider_timeout", ProviderFailureKind.GenerationTimeout)]
    [InlineData((int)HttpStatusCode.TooManyRequests, "rate_limited", ProviderFailureKind.Unavailable)]
    [InlineData((int)HttpStatusCode.InternalServerError, "provider_unavailable", ProviderFailureKind.Unavailable)]
    [InlineData((int)HttpStatusCode.BadGateway, "provider_unavailable", ProviderFailureKind.Unavailable)]
    [InlineData((int)HttpStatusCode.ServiceUnavailable, "provider_unavailable", ProviderFailureKind.Unavailable)]
    [InlineData((int)HttpStatusCode.GatewayTimeout, "provider_unavailable", ProviderFailureKind.Unavailable)]
    [InlineData((int)HttpStatusCode.NotImplemented, "provider_request_rejected", ProviderFailureKind.RejectedRequest)]
    [InlineData((int)HttpStatusCode.HttpVersionNotSupported, "provider_request_rejected", ProviderFailureKind.RejectedRequest)]
    public async Task CreateEmbeddingAsync_NormalizesProviderErrorStatus(
        int statusCodeValue,
        string expectedErrorCode,
        ProviderFailureKind expectedFailureKind)
    {
        await using var app = CreateFakeOpenAiCompatibleServer(async context =>
        {
            context.Response.StatusCode = statusCodeValue;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    message = "provider failed",
                    code = "raw_provider_code"
                }
            });
        });
        await app.StartAsync();
        using var provider = CreateEmbeddingServiceProvider(GetServerAddress(app));
        var embeddingClient = provider.GetRequiredService<IEmbeddingClient>();

        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            embeddingClient.CreateEmbeddingAsync(
                new EmbeddingRequest("hello", "embedding-model", "embedding-error-test"),
                TestContext.Current.CancellationToken));

        Assert.Equal("openai-compatible", exception.Provider);
        Assert.Equal(expectedErrorCode, exception.ErrorCode);
        Assert.Equal((HttpStatusCode)statusCodeValue, exception.StatusCode);
        Assert.Equal("raw_provider_code", exception.ProviderErrorCode);
        Assert.Equal(expectedFailureKind, exception.FailureKind);
        var expectedOutage = expectedFailureKind == ProviderFailureKind.Unavailable;
        Assert.Equal(expectedOutage, ProviderOutageExceptionClassifier.IsProviderOutage(exception));
        Assert.Equal(
            expectedOutage,
            ProviderOutageExceptionClassifier.IsProviderOutage(
                new InvalidOperationException("Outer wrapper.", exception)));
    }

    [Fact]
    public async Task CreateEmbeddingAsync_NormalizesInvalidProviderConfiguration()
    {
        using var provider = CreateEmbeddingServiceProvider("not-a-valid-uri");
        var embeddingClient = provider.GetRequiredService<IEmbeddingClient>();

        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            embeddingClient.CreateEmbeddingAsync(
                new EmbeddingRequest("hello", "embedding-model", "embedding-config-test"),
                TestContext.Current.CancellationToken));

        Assert.Equal("configuration_error", exception.ErrorCode);
        Assert.Null(exception.StatusCode);
        Assert.Equal(ProviderFailureKind.RejectedRequest, exception.FailureKind);
    }

    [Fact]
    public async Task CreateEmbeddingAsync_RejectsHttpProviderEndpointByDefault()
    {
        using var provider = CreateEmbeddingServiceProvider(
            "http://127.0.0.1:12345",
            new Dictionary<string, string?>
            {
                ["IncidentCompass:Embeddings:OpenAiCompatible:AllowInsecureHttpForLoopback"] = "false"
            });
        var embeddingClient = provider.GetRequiredService<IEmbeddingClient>();

        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            embeddingClient.CreateEmbeddingAsync(
                new EmbeddingRequest("hello", "embedding-model", "embedding-http-test"),
                TestContext.Current.CancellationToken));

        Assert.Equal("configuration_error", exception.ErrorCode);
        Assert.Null(exception.StatusCode);
        Assert.Equal(ProviderFailureKind.RejectedRequest, exception.FailureKind);
    }

    [Fact]
    public async Task CreateEmbeddingAsync_NormalizesInvalidJsonResponse()
    {
        await using var app = CreateFakeOpenAiCompatibleServer(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("not-json");
        });
        await app.StartAsync();
        using var provider = CreateEmbeddingServiceProvider(GetServerAddress(app));
        var embeddingClient = provider.GetRequiredService<IEmbeddingClient>();

        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            embeddingClient.CreateEmbeddingAsync(
                new EmbeddingRequest("hello", "embedding-model", "embedding-json-test"),
                TestContext.Current.CancellationToken));

        Assert.Equal("invalid_json", exception.ErrorCode);
        Assert.Null(exception.StatusCode);
        Assert.Equal(ProviderFailureKind.InvalidResponse, exception.FailureKind);
    }

    [Fact]
    public async Task CreateEmbeddingAsync_RejectsEmptyEmbeddingResponse()
    {
        await using var app = CreateFakeOpenAiCompatibleServer(async context =>
        {
            await context.Response.WriteAsJsonAsync(new
            {
                model = "embedding-model",
                data = new[]
                {
                    new
                    {
                        embedding = Array.Empty<float>()
                    }
                },
                usage = new
                {
                    prompt_tokens = 1,
                    total_tokens = 1
                }
            });
        });
        await app.StartAsync();
        using var provider = CreateEmbeddingServiceProvider(GetServerAddress(app));
        var embeddingClient = provider.GetRequiredService<IEmbeddingClient>();

        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            embeddingClient.CreateEmbeddingAsync(
                new EmbeddingRequest("hello", "embedding-model", "embedding-empty-test"),
                TestContext.Current.CancellationToken));

        Assert.Equal("empty_embedding", exception.ErrorCode);
        Assert.Null(exception.StatusCode);
        Assert.Equal(ProviderFailureKind.InvalidResponse, exception.FailureKind);
    }

    [Fact]
    public async Task CreateEmbeddingAsync_PropagatesCallerCancellationWithoutRetry()
    {
        await using var app = CreateFakeOpenAiCompatibleServer(async context =>
        {
            var attempts = context.RequestServices.GetRequiredService<AttemptCounter>();
            attempts.Value++;
            attempts.Started.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(5), context.RequestAborted);
        });
        await app.StartAsync();
        using var provider = CreateEmbeddingServiceProvider(
            GetServerAddress(app),
            new Dictionary<string, string?>
            {
                ["IncidentCompass:Embeddings:OpenAiCompatible:MaxRetryAttempts"] = "3"
            });
        var embeddingClient = provider.GetRequiredService<IEmbeddingClient>();
        using var cancellation = new CancellationTokenSource();
        var call = embeddingClient.CreateEmbeddingAsync(
            new EmbeddingRequest("hello", "embedding-model", "embedding-cancellation-test"),
            cancellation.Token);
        var attempts = app.Services.GetRequiredService<AttemptCounter>();
        await attempts.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

        Assert.Equal(1, attempts.Value);
    }

    [Fact]
    public async Task CreateEmbeddingAsync_NormalizesProviderTimeout()
    {
        await using var app = CreateFakeOpenAiCompatibleServer(async context =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), context.RequestAborted);
        });
        await app.StartAsync();
        using var provider = CreateEmbeddingServiceProvider(
            GetServerAddress(app),
            new Dictionary<string, string?>
            {
                ["IncidentCompass:Embeddings:OpenAiCompatible:TimeoutSeconds"] = "1"
            });
        var embeddingClient = provider.GetRequiredService<IEmbeddingClient>();

        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            embeddingClient.CreateEmbeddingAsync(
                new EmbeddingRequest("hello", "embedding-model", "embedding-timeout-test"),
                TestContext.Current.CancellationToken));

        Assert.Equal("timeout", exception.ErrorCode);
        Assert.Null(exception.StatusCode);
        Assert.Equal(ProviderFailureKind.GenerationTimeout, exception.FailureKind);
    }

    private static WebApplication CreateFakeOpenAiCompatibleServer()
    {
        return CreateFakeOpenAiCompatibleServer(async context =>
        {
            var attempts = context.RequestServices.GetRequiredService<AttemptCounter>();
            attempts.Value++;

            if (attempts.Value == 1)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = new
                    {
                        message = "rate limit",
                        code = "rate_limit"
                    }
                });
                return;
            }

            await WriteSuccessfulEmbeddingAsync(context);
        });
    }

    private static Task WriteSuccessfulEmbeddingAsync(HttpContext context)
    {
        return context.Response.WriteAsJsonAsync(new
        {
            model = "embedding-model",
            data = new[]
            {
                new
                {
                    embedding = FakeEmbeddingVector
                }
            },
            usage = new
            {
                prompt_tokens = 1,
                total_tokens = 1
            }
        });
    }

    private static WebApplication CreateFakeOpenAiCompatibleServer(Func<HttpContext, Task> handler)
    {
        var builder = LoopbackTestServer.CreateBuilder();
        builder.Services.AddSingleton<AttemptCounter>();

        var app = builder.Build();
        app.MapPost("/v1/embeddings", handler);
        return app;
    }

    private static ServiceProvider CreateEmbeddingServiceProvider(
        string baseUrl,
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var configurationValues = new Dictionary<string, string?>
        {
            ["IncidentCompass:Embeddings:Provider"] = "OpenAiCompatible",
            ["IncidentCompass:Embeddings:DefaultModel"] = "embedding-model",
            ["IncidentCompass:Embeddings:OpenAiCompatible:BaseUrl"] = baseUrl,
            ["IncidentCompass:Embeddings:OpenAiCompatible:ApiKey"] = "test-api-key",
            ["IncidentCompass:Embeddings:OpenAiCompatible:AllowInsecureHttpForLoopback"] = "true",
            ["IncidentCompass:Embeddings:OpenAiCompatible:MaxRetryAttempts"] = "0",
            ["IncidentCompass:Embeddings:OpenAiCompatible:RetryBaseDelayMilliseconds"] = "1",
            ["IncidentCompass:Embeddings:OpenAiCompatible:TimeoutSeconds"] = "30"
        };

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                configurationValues[key] = value;
            }
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    private static string GetServerAddress(WebApplication app)
    {
        return LoopbackTestServer.GetAddress(app);
    }

    private sealed class AttemptCounter
    {
        public int Value { get; set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
