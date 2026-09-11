using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.Embeddings.OpenAi;
using IncidentCompass.Infrastructure.ModelGateway.OpenAi;
using IncidentCompass.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// A route's provider decides which endpoint answers it and which credential it presents. These
/// tests stand up two loopback servers that record what they received, so "reached the right
/// provider" is asserted from the server's side rather than from what the client believed it was
/// about to do.
/// <para>
/// The credential assertions are the point of the two-server shape. A gateway that routes correctly
/// but presents one host-wide key to both endpoints has handed provider A's credential to provider
/// B, which is a worse failure than not routing at all, and only a second server can see it.
/// </para>
/// </summary>
public sealed class MultiProviderModelGatewayTests
{
    private const string FirstSecretRef = "FIRST_PROVIDER_API_KEY";
    private const string SecondSecretRef = "SECOND_PROVIDER_API_KEY";
    private const string FirstApiKey = "sk-first-provider-key";
    private const string SecondApiKey = "sk-second-provider-key";
    private const string HostWideApiKey = "sk-host-wide-key";

    private static readonly float[] EmbeddingVector = [0.1f, 0.2f];

    [Fact]
    public async Task ChatCall_ReachesTheEndpointItsRouteProviderNames()
    {
        await using var first = CreateChatServer("first-provider-answer");
        await using var second = CreateChatServer("second-provider-answer");
        await first.StartAsync(TestContext.Current.CancellationToken);
        await second.StartAsync(TestContext.Current.CancellationToken);

        var modelClient = CreateModelClient(
            TwoProviders(LoopbackTestServer.GetAddress(first), LoopbackTestServer.GetAddress(second)));

        var firstResponse = await modelClient.CompleteAsync(
            CreateChatRequest("local-oai"),
            TestContext.Current.CancellationToken);
        var secondResponse = await modelClient.CompleteAsync(
            CreateChatRequest("remote-oai"),
            TestContext.Current.CancellationToken);

        Assert.Equal("first-provider-answer", firstResponse.Content);
        Assert.Equal("second-provider-answer", secondResponse.Content);
        Assert.Equal(1, RequestLogOf(first).Count);
        Assert.Equal(1, RequestLogOf(second).Count);
    }

    [Fact]
    public async Task ChatCall_PresentsOnlyItsOwnProviderCredential()
    {
        await using var first = CreateChatServer("first-provider-answer");
        await using var second = CreateChatServer("second-provider-answer");
        await first.StartAsync(TestContext.Current.CancellationToken);
        await second.StartAsync(TestContext.Current.CancellationToken);

        var modelClient = CreateModelClient(
            TwoProviders(LoopbackTestServer.GetAddress(first), LoopbackTestServer.GetAddress(second)));

        await modelClient.CompleteAsync(CreateChatRequest("local-oai"), TestContext.Current.CancellationToken);
        await modelClient.CompleteAsync(CreateChatRequest("remote-oai"), TestContext.Current.CancellationToken);

        Assert.Equal("Bearer " + FirstApiKey, Assert.Single(RequestLogOf(first).Authorizations));
        Assert.Equal("Bearer " + SecondApiKey, Assert.Single(RequestLogOf(second).Authorizations));
        Assert.DoesNotContain(SecondApiKey, RequestLogOf(first).Authorizations);
        Assert.DoesNotContain(HostWideApiKey, RequestLogOf(second).Authorizations);
    }

    [Fact]
    public async Task EmbeddingCall_ReachesTheEndpointItsRouteProviderNamesWithItsOwnCredential()
    {
        await using var first = CreateEmbeddingServer();
        await using var second = CreateEmbeddingServer();
        await first.StartAsync(TestContext.Current.CancellationToken);
        await second.StartAsync(TestContext.Current.CancellationToken);

        var embeddingClient = CreateEmbeddingClient(
            TwoProviders(LoopbackTestServer.GetAddress(first), LoopbackTestServer.GetAddress(second)));

        await embeddingClient.CreateEmbeddingAsync(
            new EmbeddingRequest("hello", "embedding-model", "embedding-routing-test", "remote-oai"),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, RequestLogOf(first).Count);
        Assert.Equal(1, RequestLogOf(second).Count);
        Assert.Equal("Bearer " + SecondApiKey, Assert.Single(RequestLogOf(second).Authorizations));
    }

    /// <summary>
    /// The shipped shape: one provider, whose endpoint matches the host-wide <c>BaseUrl</c> and
    /// whose <c>ApiKeySecretRef</c> names a variable an operator running from a plain checkout has
    /// not set. The host-wide credential still applies, so nothing an operator does today changes.
    /// </summary>
    [Fact]
    public async Task SingleProviderConfiguration_StillUsesTheHostWideCredential()
    {
        await using var server = CreateChatServer("shipped-answer");
        await server.StartAsync(TestContext.Current.CancellationToken);
        var address = LoopbackTestServer.GetAddress(server);

        var providers = new Dictionary<string, TriageProviderSettings>(StringComparer.Ordinal)
        {
            ["local-oai"] = new("OpenAICompatible", address, FirstSecretRef)
        };
        var modelClient = CreateModelClient(providers, secrets: new Dictionary<string, string>(StringComparer.Ordinal));

        var response = await modelClient.CompleteAsync(
            CreateChatRequest("local-oai"),
            TestContext.Current.CancellationToken);

        Assert.Equal("shipped-answer", response.Content);
        Assert.Equal("Bearer " + HostWideApiKey, Assert.Single(RequestLogOf(server).Authorizations));
    }

    /// <summary>
    /// A caller with no route provider, which is every direct <c>IAiModelClient</c> user outside the
    /// governed investigation path, keeps reaching the host-wide endpoint and credential without the
    /// triage configuration being read at all. The stub repository in the helper throws if it is
    /// read, so this asserts the absence of that read rather than assuming it.
    /// </summary>
    [Fact]
    public async Task ChatCall_WithNoRouteProvider_UsesTheHostWideProfileWithoutReadingConfiguration()
    {
        await using var server = CreateChatServer("host-wide-answer");
        await server.StartAsync(TestContext.Current.CancellationToken);
        var address = LoopbackTestServer.GetAddress(server);

        var modelClient = new OpenAiCompatibleModelClient(
            new HttpClient(),
            Options.Create(HostOptions(address)),
            TestModelProviderProfiles.CreateResolver());

        var response = await modelClient.CompleteAsync(
            new AiModelRequest(
                CorrelationId: "no-provider-test",
                Model: "test-model",
                Messages: [new AiChatMessage(AiMessageRole.User, "hello")]),
            TestContext.Current.CancellationToken);

        Assert.Equal("host-wide-answer", response.Content);
        Assert.Equal("Bearer " + HostWideApiKey, Assert.Single(RequestLogOf(server).Authorizations));
    }

    [Fact]
    public async Task ChatCall_ForAnUnknownProviderFailsClosedWithoutNamingACredential()
    {
        var modelClient = CreateModelClient(
            TwoProviders("https://provider-a.example", "https://provider-b.example"));

        var exception = await Assert.ThrowsAsync<AiModelException>(() =>
            modelClient.CompleteAsync(
                CreateChatRequest("does-not-exist"),
                TestContext.Current.CancellationToken));

        Assert.Equal("configuration_error", exception.ErrorCode);
        Assert.Equal(ProviderFailureKind.RejectedRequest, exception.FailureKind);
        Assert.Contains("does-not-exist", exception.Message, StringComparison.Ordinal);
        AssertNoCredential(exception.ToString());
    }

    /// <summary>
    /// Two providers means no host-wide credential fallback, so the second provider's unset secret
    /// ref fails the call rather than silently borrowing the first provider's key. The message names
    /// the variable, which is the actionable part, and nothing else.
    /// </summary>
    [Fact]
    public async Task ChatCall_ForAProviderWithAnUnresolvedSecretFailsClosedWithoutNamingACredential()
    {
        var modelClient = CreateModelClient(
            TwoProviders("https://provider-a.example", "https://provider-b.example"),
            secrets: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [FirstSecretRef] = FirstApiKey
            });

        var exception = await Assert.ThrowsAsync<AiModelException>(() =>
            modelClient.CompleteAsync(
                CreateChatRequest("remote-oai"),
                TestContext.Current.CancellationToken));

        Assert.Equal("configuration_error", exception.ErrorCode);
        Assert.Contains(SecondSecretRef, exception.Message, StringComparison.Ordinal);
        AssertNoCredential(exception.ToString());
    }

    /// <summary>
    /// A provider failure carries a status and a normalized code, and never the credential the
    /// request presented. This is the path a real outage takes into the ledger and the logs.
    /// </summary>
    [Fact]
    public async Task ProviderFailure_CarriesNoCredentialIntoTheNormalizedException()
    {
        await using var server = CreateFailingChatServer();
        await server.StartAsync(TestContext.Current.CancellationToken);
        var address = LoopbackTestServer.GetAddress(server);

        var providers = new Dictionary<string, TriageProviderSettings>(StringComparer.Ordinal)
        {
            ["local-oai"] = new("OpenAICompatible", address, FirstSecretRef),
            ["remote-oai"] = new("OpenAICompatible", address, SecondSecretRef)
        };
        var modelClient = CreateModelClient(providers);

        var exception = await Assert.ThrowsAsync<AiModelException>(() =>
            modelClient.CompleteAsync(
                CreateChatRequest("remote-oai"),
                TestContext.Current.CancellationToken));

        Assert.Equal("invalid_request", exception.ErrorCode);
        Assert.Equal(ProviderFailureKind.RejectedRequest, exception.FailureKind);
        AssertNoCredential(exception.ToString());
    }

    /// <summary>
    /// The two types that hold a resolved credential print their type name and nothing else. A
    /// record here would have printed the credential from any interpolation in the process, so this
    /// pins the choice rather than leaving it to the next person who reaches for a record.
    /// </summary>
    [Fact]
    public void ResolvedProviderTypes_DoNotPrintTheirCredential()
    {
        var profile = new Infrastructure.OpenAiCompatible.OpenAiCompatibleProviderProfile(
            new Uri("https://provider-a.example/v1/chat/completions"),
            FirstApiKey);
        var defaults = new Infrastructure.OpenAiCompatible.OpenAiCompatibleProviderDefaults(
            "https://provider-a.example",
            HostWideApiKey,
            "/v1/chat/completions",
            allowInsecureHttpForLoopback: false);

        AssertNoCredential(profile.ToString());
        AssertNoCredential(defaults.ToString());
        AssertNoCredential($"{profile} {defaults}");
    }

    private static void AssertNoCredential(string text)
    {
        Assert.DoesNotContain(FirstApiKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SecondApiKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain(HostWideApiKey, text, StringComparison.Ordinal);
    }

    private static Dictionary<string, TriageProviderSettings> TwoProviders(
        string firstEndpoint,
        string secondEndpoint)
    {
        return new Dictionary<string, TriageProviderSettings>(StringComparer.Ordinal)
        {
            ["local-oai"] = new("OpenAICompatible", firstEndpoint, FirstSecretRef),
            ["remote-oai"] = new("OpenAICompatible", secondEndpoint, SecondSecretRef)
        };
    }

    private static OpenAiCompatibleModelClient CreateModelClient(
        IReadOnlyDictionary<string, TriageProviderSettings> providers,
        IReadOnlyDictionary<string, string>? secrets = null)
    {
        return new OpenAiCompatibleModelClient(
            new HttpClient(),
            Options.Create(HostOptions("http://127.0.0.1:1")),
            TestModelProviderProfiles.CreateResolver(providers, secrets ?? DefaultSecrets()));
    }

    private static OpenAiCompatibleEmbeddingClient CreateEmbeddingClient(
        IReadOnlyDictionary<string, TriageProviderSettings> providers)
    {
        return new OpenAiCompatibleEmbeddingClient(
            new HttpClient(),
            Options.Create(new OpenAiCompatibleEmbeddingClientOptions
            {
                BaseUrl = "http://127.0.0.1:1",
                ApiKey = HostWideApiKey,
                MaxRetryAttempts = 0,
                AllowInsecureHttpForLoopback = true
            }),
            TestModelProviderProfiles.CreateResolver(providers, DefaultSecrets()));
    }

    private static OpenAiCompatibleModelClientOptions HostOptions(string baseUrl) => new()
    {
        BaseUrl = baseUrl,
        ApiKey = HostWideApiKey,
        MaxRetryAttempts = 0,
        AllowInsecureHttpForLoopback = true
    };

    private static Dictionary<string, string> DefaultSecrets() => new(StringComparer.Ordinal)
    {
        [FirstSecretRef] = FirstApiKey,
        [SecondSecretRef] = SecondApiKey
    };

    private static AiModelRequest CreateChatRequest(string providerId) =>
        new(
            CorrelationId: "multi-provider-test",
            Model: "test-model",
            Messages: [new AiChatMessage(AiMessageRole.User, "hello")],
            ProviderId: providerId);

    private static ProviderRequestLog RequestLogOf(WebApplication app) =>
        app.Services.GetRequiredService<ProviderRequestLog>();

    private static WebApplication CreateChatServer(string answer)
    {
        var builder = LoopbackTestServer.CreateBuilder();
        builder.Services.AddSingleton<ProviderRequestLog>();
        var app = builder.Build();
        app.MapPost("/v1/chat/completions", async context =>
        {
            context.RequestServices.GetRequiredService<ProviderRequestLog>()
                .Record(context.Request.Headers.Authorization.ToString());
            await context.Response.WriteAsJsonAsync(new
            {
                model = "test-model",
                choices = new[] { new { message = new { role = "assistant", content = answer } } },
                usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 }
            });
        });
        return app;
    }

    private static WebApplication CreateFailingChatServer()
    {
        var builder = LoopbackTestServer.CreateBuilder();
        builder.Services.AddSingleton<ProviderRequestLog>();
        var app = builder.Build();
        app.MapPost("/v1/chat/completions", async context =>
        {
            context.RequestServices.GetRequiredService<ProviderRequestLog>()
                .Record(context.Request.Headers.Authorization.ToString());
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new { message = "bad request", code = "raw_provider_code" }
            });
        });
        return app;
    }

    private static WebApplication CreateEmbeddingServer()
    {
        var builder = LoopbackTestServer.CreateBuilder();
        builder.Services.AddSingleton<ProviderRequestLog>();
        var app = builder.Build();
        app.MapPost("/v1/embeddings", async context =>
        {
            context.RequestServices.GetRequiredService<ProviderRequestLog>()
                .Record(context.Request.Headers.Authorization.ToString());
            await context.Response.WriteAsJsonAsync(new
            {
                model = "embedding-model",
                data = new[] { new { embedding = EmbeddingVector } },
                usage = new { prompt_tokens = 1, total_tokens = 1 }
            });
        });
        return app;
    }
}
