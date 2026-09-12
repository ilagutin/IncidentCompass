using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using IncidentCompass.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IncidentCompass.IntegrationTests;

[Collection(ApiKeyAuthenticationCollection.CollectionName)]
public sealed class ApiKeyAuthenticationTests
{
    private const string KeyA = "api_key_A_abcdefghijklmnopqrstuvwxyz123456";
    private const string KeyB = "api_key_B_abcdefghijklmnopqrstuvwxyz123456";

    public static IEnumerable<object[]> RejectedHeaderCases =>
    [
        [Array.Empty<string>()],
        [new[] { "short" }],
        [new[] { new string('a', 31) }],
        [new[] { new string('a', 129) }],
        [new[] { "api_key_A_abcdefghijklmnopqrstuvwxyz12345=" }],
        [new[] { "api key A abcdefghijklmnopqrstuvwxyz123456" }],
        [new[] { KeyA + " " }],
        [new[] { "api_key_é_abcdefghijklmnopqrstuvwxyz123456" }],
        [new[] { "api_key_A_abcdefghijklmnopqrstuvwxyz123456,api_key_B_abcdefghijklmnopqrstuvwxyz123456" }],
        [new[] { KeyA, KeyB }],
        [new[] { "api_key_X_abcdefghijklmnopqrstuvwxyz123456" }]
    ];

    public static IEnumerable<object[]> ProtectedRouteCases =>
    [
        [HttpMethod.Get, "/api/v1/users/me"],
        [HttpMethod.Get, "/api/v1/faults/11111111-1111-1111-1111-111111111111"],
        [HttpMethod.Get, "/api/v1/faults/11111111-1111-1111-1111-111111111111/ledger"],
        [HttpMethod.Get, "/api/v1/triage-reports"],
        [HttpMethod.Get, "/api/v1/triage-reports/11111111-1111-1111-1111-111111111111"],
        [HttpMethod.Get, "/api/v1/faults/11111111-1111-1111-1111-111111111111/triage-report"],
        [HttpMethod.Get, "/api/v1/action-approvals"],
        [HttpMethod.Get, "/api/v1/action-approvals/11111111-1111-1111-1111-111111111111"],
        [HttpMethod.Get, "/api/v1/observability/cost-rollups?fromUtc=2026-08-01T00:00:00Z&toUtc=2026-08-01T01:00:00Z"],
        [HttpMethod.Post, "/api/v1/action-approvals/11111111-1111-1111-1111-111111111111/approve"],
        [HttpMethod.Post, "/api/v1/action-approvals/11111111-1111-1111-1111-111111111111/reject"],
        [HttpMethod.Post, "/api/v1/incidents"],
        [HttpMethod.Post, "/v1/traces"],
        [HttpMethod.Post, "/v1/logs"]
    ];

    [Theory]
    [MemberData(nameof(RejectedHeaderCases))]
    public async Task ProtectedRoutes_RejectMissingMalformedRepeatedAndInvalidKeysBeforeBinding(string[] values)
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/incidents")
        {
            Content = new StringContent("not-json", Encoding.UTF8, "application/json")
        };
        foreach (var value in values)
        {
            request.Headers.TryAddWithoutValidation("X-IncidentCompass-Key", value);
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ProtectedRouteCases))]
    public async Task EveryCurrentDataAndOtlpRouteRejectsMissingKey(HttpMethod method, string route)
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        using var request = new HttpRequestMessage(method, route);
        if (method == HttpMethod.Post)
        {
            request.Content = new StringContent("not-a-valid-payload", Encoding.UTF8, "application/json");
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AcceptedKeyCreatesOnlyServerOwnedIdentityAndIgnoresDemoHeaders()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/users/me");
        request.Headers.Add("X-IncidentCompass-Key", KeyA);
        request.Headers.Add("X-Demo-User-Id", "spoofed-user");
        request.Headers.Add("X-Demo-Tenant-Id", "spoofed-tenant");
        request.Headers.Add("X-Demo-Roles", "admin");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<CurrentUserResponse>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.True(body.IsAuthenticated);
        Assert.Equal("key-a", body.UserId);
        Assert.Equal("tenant-a", body.TenantId);
        Assert.Empty(body.Roles);
        Assert.Empty(body.Groups);
    }

    [Fact]
    public async Task AuthDisabledDevelopmentKeepsDemoUserContext()
    {
        using var factory = new MockProvidersWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(static logging => logging.ClearProviders());
            builder.UseEnvironment("Development");
            builder.UseSetting("IncidentCompass:ApiKeyAuth:Enabled", "false");
        });
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/api/v1/users/me", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<CurrentUserResponse>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("demo-user", body!.UserId);
        Assert.Equal("demo-tenant", body.TenantId);
    }

    [Fact]
    public void ApiKeyCompositionReplacesForegroundUserAndTenantContexts()
    {
        using var factory = CreateFactory();
        _ = CreateClient(factory);
        using var scope = factory.Services.CreateScope();

        Assert.Equal(
            "ApiKeyUserContext",
            scope.ServiceProvider.GetRequiredService<IncidentCompass.Application.Core.Security.IUserContext>().GetType().Name);
        Assert.Equal(
            "ApiKeyIncidentTenantContext",
            scope.ServiceProvider.GetRequiredService<IncidentCompass.Application.Core.Tenancy.IIncidentTenantContext>().GetType().Name);
    }

    [Fact]
    public async Task FixedWindowLimiterPartitionsByAuthenticatedKeyAndDoesNotLimitAnonymousHealth()
    {
        using var factory = CreateFactory(permitLimit: 2);
        using var client = CreateClient(factory);

        Assert.Equal(HttpStatusCode.OK, (await SendWithKey(client, "/api/v1/users/me", KeyA)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendWithKey(client, "/api/v1/users/me", KeyA)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendWithKey(client, "/api/v1/users/me", KeyA)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendWithKey(client, "/api/v1/users/me", KeyB)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendWithKey(client, "/api/v1/users/me", "api_key_X_abcdefghijklmnopqrstuvwxyz123456")).StatusCode);

        for (var request = 0; request < 5; request++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/health", TestContext.Current.CancellationToken)).StatusCode);
        }
    }

    [Fact]
    public void EndpointMetadata_AllowsAnonymousOnlyForHealthAndDevelopmentOpenApi()
    {
        using var factory = CreateFactory();
        _ = CreateClient(factory);
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "/health",
            "/api/v1/health",
            "/api/v1/health/memory-sync",
            "/api/v1/health/memory-corpus",
            "/openapi/{documentName}.json"
        };
        var routes = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        var anonymousRoutes = routes
            .Where(static endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(static endpoint => endpoint.RoutePattern.RawText!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Subset(allowed, anonymousRoutes);
        Assert.DoesNotContain(routes, endpoint =>
            endpoint.RoutePattern.RawText is { } route &&
            (route.StartsWith("/api/v1", StringComparison.Ordinal) || route.StartsWith("/v1/", StringComparison.Ordinal)) &&
            !allowed.Contains(route) &&
            endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null);
    }

    [Fact]
    public async Task RejectionsEmitOnlyBoundedOutcomeMetricsAndDoNotEchoRawOrDigestSecret()
    {
        const string presentedSentinel = "api_key_LEAK_abcdefghijklmnopqrstuvwxyz12345";
        var measurements = new List<KeyValuePair<string, object?>[]>();
        var capturedLogs = new List<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == ApiAuthenticationMetrics.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => measurements.Add(tags.ToArray()));
        listener.Start();
        using var factory = CreateFactory(capturedLogs: capturedLogs);
        using var client = CreateClient(factory);

        var response = await SendWithKey(client, "/api/v1/users/me", presentedSentinel);
        var responseText = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(measurements, tags => tags.Any(tag => tag.Key == "outcome" && Equals(tag.Value, "invalid")));
        var captured = string.Join(" ", measurements.SelectMany(static tags => tags).Select(static tag => $"{tag.Key}:{tag.Value}"));
        Assert.DoesNotContain(KeyA, captured, StringComparison.Ordinal);
        Assert.DoesNotContain(Digest(KeyA), captured, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(presentedSentinel, captured, StringComparison.Ordinal);
        Assert.DoesNotContain(Digest(presentedSentinel), captured, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(KeyA, responseText, StringComparison.Ordinal);
        Assert.DoesNotContain(Digest(KeyA), responseText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(presentedSentinel, responseText, StringComparison.Ordinal);
        Assert.DoesNotContain(Digest(presentedSentinel), responseText, StringComparison.OrdinalIgnoreCase);
        var logText = string.Join(" ", capturedLogs);
        Assert.DoesNotContain(KeyA, logText, StringComparison.Ordinal);
        Assert.DoesNotContain(Digest(KeyA), logText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(presentedSentinel, logText, StringComparison.Ordinal);
        Assert.DoesNotContain(Digest(presentedSentinel), logText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnonymousAllowlistReturnsSuccessWithoutAuthenticationRejectionMetrics()
    {
        var measurementCount = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == ApiAuthenticationMetrics.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => Interlocked.Increment(ref measurementCount));
        listener.Start();
        using var factory = CreateFactory(clearHealthChecks: true);
        using var client = CreateClient(factory);

        foreach (var route in new[]
        {
            "/health",
            "/api/v1/health",
            "/api/v1/health/memory-sync",
            "/openapi/v1.json"
        })
        {
            var response = await client.GetAsync(route, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(0, Volatile.Read(ref measurementCount));
    }

    private static WebApplicationFactory<Program> CreateFactory(
        int permitLimit = 20,
        List<string>? capturedLogs = null,
        bool clearHealthChecks = false)
    {
        return new MockProvidersWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                if (capturedLogs is not null)
                {
                    logging.AddProvider(new CapturingLoggerProvider(capturedLogs));
                }
            });
            builder.UseEnvironment("Development");
            builder.UseSetting("IncidentCompass:ApiKeyAuth:Enabled", "true");
            builder.UseSetting("IncidentCompass:ApiKeyAuth:PermitLimit", permitLimit.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("IncidentCompass:ApiKeyAuth:WindowSeconds", "300");
            SetCredential(builder, 0, "key-a", "tenant-a", KeyA);
            SetCredential(builder, 1, "key-b", "tenant-b", KeyB);
            if (clearHealthChecks)
            {
                builder.ConfigureTestServices(services =>
                    services.PostConfigure<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckServiceOptions>(
                        static options => options.Registrations.Clear()));
            }
        });
    }

    private static void SetCredential(IWebHostBuilder builder, int index, string keyId, string tenantId, string key)
    {
        var prefix = $"IncidentCompass:ApiKeyAuth:Credentials:{index}";
        builder.UseSetting($"{prefix}:KeyId", keyId);
        builder.UseSetting($"{prefix}:TenantId", tenantId);
        builder.UseSetting($"{prefix}:Sha256Digest", Digest(key));
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    private static async Task<HttpResponseMessage> SendWithKey(HttpClient client, string route, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Add("X-IncidentCompass-Key", key);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value)));

    private sealed record CurrentUserResponse(
        bool IsAuthenticated,
        string? UserId,
        string? TenantId,
        IReadOnlyCollection<string> Roles,
        IReadOnlyCollection<string> Groups);

    private sealed class CapturingLoggerProvider(List<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(messages);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(List<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (messages)
            {
                messages.Add(formatter(state, exception));
                if (exception is not null)
                {
                    messages.Add(exception.ToString());
                }
            }
        }
    }
}
