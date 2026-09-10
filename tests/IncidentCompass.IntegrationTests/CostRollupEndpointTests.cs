using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IncidentCompass.Infrastructure.Observability;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class CostRollupEndpointTests(PostgresRepositoryFixture postgres)
{
    private const string KeyA = "cost_rollup_key_A_abcdefghijklmnopqrstuvwxyz123456";
    private const string KeyB = "cost_rollup_key_B_abcdefghijklmnopqrstuvwxyz123456";

    [DockerAvailableFact]
    public async Task EndpointIsTenantScopedIndexedAndReturnsOnlyAggregateData()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var tenantA = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString, "tenant-a");
        var tenantB = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString, "tenant-b");
        await SeedPriceAsync(database.ConnectionString);
        await SeedCallAsync(database.ConnectionString, tenantA, "2026-08-03T10:30:00Z", 100, 200, 300);
        await SeedCallAsync(database.ConnectionString, tenantB, "2026-08-03T11:30:00Z", 400, 500, 900);
        var capturedLogs = new List<string>();
        using var factory = CreateFactory(database.ConnectionString, capturedLogs);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        using var tenantAResponse = await SendAsync(
            client, KeyA, Window("2026-08-03T10:00:00Z", "2026-08-03T11:00:00Z"));
        using var foreignResponse = await SendAsync(
            client, KeyA, Window("2026-08-03T11:00:00Z", "2026-08-03T12:00:00Z"));
        using var absentResponse = await SendAsync(
            client, KeyA, Window("2026-08-03T12:00:00Z", "2026-08-03T13:00:00Z"));
        using var tenantBResponse = await SendAsync(
            client, KeyB, Window("2026-08-03T11:00:00Z", "2026-08-03T12:00:00Z"));

        var tenantABody = await tenantAResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var foreignBody = await foreignResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var absentBody = await absentResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var tenantBBody = await tenantBResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, tenantAResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, foreignResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, absentResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, tenantBResponse.StatusCode);
        AssertHour(tenantABody, 100, 200, 300, 0.0005m);
        Assert.Empty(JsonDocument.Parse(foreignBody).RootElement.GetProperty("hours").EnumerateArray());
        Assert.Empty(JsonDocument.Parse(absentBody).RootElement.GetProperty("hours").EnumerateArray());
        AssertHour(tenantBBody, 400, 500, 900, 0.0014m);

        var publicAndLogs = tenantABody + foreignBody + absentBody + tenantBBody + string.Join(' ', capturedLogs);
        foreach (var forbidden in new[]
                 {
                     "tenant-a", "tenant-b", "private-provider", "private-model", "safe-private-route",
                     "prompt-sentinel", "incident-body-sentinel", "credential-sentinel",
                     "provider-base-url-sentinel", "embedding-vector-sentinel"
                 })
        {
            Assert.DoesNotContain(forbidden, publicAndLogs, StringComparison.Ordinal);
        }

        var plan = await ReadQueryPlanAsync(database.ConnectionString);
        Assert.Contains("ix_triage_ledger_model_call_fault_created_at", plan, StringComparison.Ordinal);
    }

    [DockerAvailableFact]
    public async Task InvalidWindowsUseTheCentralValidationProblemMapping()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var capturedLogs = new List<string>();
        using var factory = CreateFactory(database.ConnectionString, capturedLogs);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        var routes = new[]
        {
            Window("2026-08-03T10:00:00%2B01:00", "2026-08-03T11:00:00Z"),
            Window("2026-08-03T11:00:00Z", "2026-08-03T10:00:00Z"),
            Window("2026-08-03T10:00:00Z", "2026-08-03T10:00:00Z"),
            Window("2026-08-01T00:00:00Z", "2026-09-01T00:00:00.0000001Z")
        };

        foreach (var route in routes)
        {
            using var response = await SendAsync(client, KeyA, route);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Request validation failed", body, StringComparison.Ordinal);
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string connectionString,
        List<string> capturedLogs) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(new CapturingLoggerProvider(capturedLogs));
            });
            builder.UseExplicitMockProviders();
            builder.UseSetting("ConnectionStrings:CostRollupEndpointTests", connectionString);
            builder.UseSetting("IncidentCompass:Postgres:ConnectionStringName", "CostRollupEndpointTests");
            builder.UseSetting("IncidentCompass:ApiKeyAuth:Enabled", "true");
            builder.UseSetting("IncidentCompass:ApiKeyAuth:PermitLimit", "100");
            AddCredential(builder, 0, "cost-key-a", "tenant-a", KeyA);
            AddCredential(builder, 1, "cost-key-b", "tenant-b", KeyB);
            builder.ConfigureTestServices(static services => services.RemoveAll<IHostedService>());
        });

    private static void AddCredential(
        IWebHostBuilder builder,
        int index,
        string keyId,
        string tenantId,
        string key)
    {
        builder.UseSetting($"IncidentCompass:ApiKeyAuth:Credentials:{index}:KeyId", keyId);
        builder.UseSetting($"IncidentCompass:ApiKeyAuth:Credentials:{index}:TenantId", tenantId);
        builder.UseSetting(
            $"IncidentCompass:ApiKeyAuth:Credentials:{index}:Sha256Digest",
            Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(key))));
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string key, string route)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Add("X-IncidentCompass-Key", key);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string Window(string fromUtc, string toUtc) =>
        $"/api/v1/observability/cost-rollups?fromUtc={fromUtc}&toUtc={toUtc}";

    private static async Task SeedPriceAsync(string connectionString) =>
        await ActionApprovalTestSupport.ExecuteAsync(connectionString, """
            INSERT INTO incidentcompass.ai_model_pricing (
                id, provider, model, currency, input_token_price_per_million,
                output_token_price_per_million, effective_from_utc, effective_to_utc)
            VALUES (@id, 'private-provider', 'private-model', 'USD', 1, 2,
                '2026-08-03T00:00:00Z', '2026-08-04T00:00:00Z');
            """, ("id", Guid.NewGuid()));

    private static Task SeedCallAsync(
        string connectionString,
        ActionApprovalOriginFixture origin,
        string atUtc,
        int input,
        int output,
        int total) =>
        ActionApprovalTestSupport.ExecuteAsync(connectionString, """
            INSERT INTO incidentcompass.triage_ledger (
                fault_id, job_id, attempt, event_type, rationale, config_hash, created_at_utc)
            VALUES (@fault, @job, 1, 'ModelCall', @rationale, @config, @at);
            """,
            ("fault", origin.FaultId), ("job", origin.JobId), ("config", origin.ConfigHash),
            ("at", DateTimeOffset.Parse(atUtc, CultureInfo.InvariantCulture)),
            ("rationale", JsonSerializer.Serialize(new
            {
                kind = "orchestrator",
                routeId = "safe-private-route",
                provider = "openai-compatible",
                model = "private-model",
                usageSource = "provider",
                inputTokens = input,
                outputTokens = output,
                totalTokens = total,
                providerId = "private-provider"
            })));

    private static void AssertHour(string body, long input, long output, long total, decimal amount)
    {
        using var document = JsonDocument.Parse(body);
        var hour = Assert.Single(document.RootElement.GetProperty("hours").EnumerateArray());
        Assert.Equal(1, hour.GetProperty("callCount").GetInt64());
        Assert.Equal(input, hour.GetProperty("inputTokens").GetInt64());
        Assert.Equal(output, hour.GetProperty("outputTokens").GetInt64());
        Assert.Equal(total, hour.GetProperty("totalTokens").GetInt64());
        Assert.Equal(1, hour.GetProperty("pricedCallCount").GetInt64());
        Assert.Equal(0, hour.GetProperty("unpricedCallCount").GetInt64());

        // The spend above is provider-reported throughout, and the response says so rather than
        // leaving a reader to assume it.
        Assert.Equal(0, hour.GetProperty("estimatedUsageCallCount").GetInt64());
        Assert.Equal(0, hour.GetProperty("estimatedUsageTotalTokens").GetInt64());
        var spend = Assert.Single(hour.GetProperty("spendTotals").EnumerateArray());
        Assert.Equal("USD", spend.GetProperty("currency").GetString());
        Assert.Equal(amount, spend.GetProperty("amount").GetDecimal());
    }

    private static async Task<string> ReadQueryPlanAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SET enable_seqscan = off; EXPLAIN (COSTS OFF) " + PostgresModelCostRollupRepository.ModelCallsSql,
            connection);
        command.Parameters.AddWithValue("tenant_id", "tenant-a");
        command.Parameters.AddWithValue("from_utc", DateTimeOffset.Parse("2026-08-03T10:00:00Z", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("to_utc", DateTimeOffset.Parse("2026-08-03T11:00:00Z", CultureInfo.InvariantCulture));
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken)) lines.Add(reader.GetString(0));
        return string.Join('\n', lines);
    }

    private sealed class CapturingLoggerProvider(List<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(messages);
        public void Dispose() { }
    }

    private sealed class CapturingLogger(List<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (messages) messages.Add(formatter(state, exception));
        }
    }
}
