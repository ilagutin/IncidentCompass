using System.Globalization;
using System.Net;
using Google.Protobuf;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// the OTLP byte cap does not bound how many records a compact protobuf export carries,
/// so a separate per-request record bound rejects an over-limit export before any command is
/// dispatched. These tests hold the all-or-nothing property: an over-limit export must leave no
/// signal, fault or triage job behind, and an export exactly at the limit must still be ingested.
/// A small configured limit keeps the Docker-backed cases cheap.
/// </summary>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class OtlpExportLimitTests(PostgresRepositoryFixture postgres)
{
    private const int TestLimit = 3;

    [DockerAvailableFact]
    public async Task OtlpTraceExport_AboveTheSignalLimit_IsRejectedWithoutCreatingFaultsOrJobs()
    {
        using var scope = await CreateScopeAsync();
        var service = "otlp-limit-traces-" + Guid.NewGuid().ToString("N");

        var response = await PostAsync(
            scope.Client,
            "/v1/traces",
            OtlpExportTestFactory.TraceExport(service, TestLimit + 1).ToByteArray());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0L, await CountSignalsAsync(scope.ConnectionString, service));
        Assert.Equal(0L, await CountFaultsAsync(scope.ConnectionString, service));
        Assert.Equal(0L, await CountJobsAsync(scope.ConnectionString, service));
    }

    [DockerAvailableFact]
    public async Task OtlpTraceExport_ExactlyAtTheSignalLimit_IsIngested()
    {
        using var scope = await CreateScopeAsync();
        var service = "otlp-limit-traces-at-" + Guid.NewGuid().ToString("N");

        var response = await PostAsync(
            scope.Client,
            "/v1/traces",
            OtlpExportTestFactory.TraceExport(service, TestLimit).ToByteArray());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((long)TestLimit, await CountSignalsAsync(scope.ConnectionString, service));
        Assert.Equal(1L, await CountFaultsAsync(scope.ConnectionString, service));
        Assert.Equal(1L, await CountJobsAsync(scope.ConnectionString, service));
    }

    [DockerAvailableFact]
    public async Task OtlpLogExport_AboveTheSignalLimit_IsRejectedWithoutCreatingFaultsOrJobs()
    {
        using var scope = await CreateScopeAsync();
        var service = "otlp-limit-logs-" + Guid.NewGuid().ToString("N");

        var response = await PostAsync(
            scope.Client,
            "/v1/logs",
            OtlpExportTestFactory.LogExport(service, TestLimit + 1).ToByteArray());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0L, await CountSignalsAsync(scope.ConnectionString, service));
        Assert.Equal(0L, await CountFaultsAsync(scope.ConnectionString, service));
        Assert.Equal(0L, await CountJobsAsync(scope.ConnectionString, service));
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string route, byte[] payload)
    {
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-protobuf");
        return await client.PostAsync(route, content, TestContext.Current.CancellationToken);
    }

    private static Task<long> CountSignalsAsync(string connectionString, string serviceName) =>
        ScalarAsync(
            connectionString,
            "SELECT COUNT(*) FROM incidentcompass.signals WHERE service_name = @service_name;",
            serviceName);

    private static Task<long> CountFaultsAsync(string connectionString, string serviceName) =>
        ScalarAsync(
            connectionString,
            "SELECT COUNT(*) FROM incidentcompass.faults WHERE service_name = @service_name;",
            serviceName);

    private static Task<long> CountJobsAsync(string connectionString, string serviceName) =>
        ScalarAsync(
            connectionString,
            """
            SELECT COUNT(*)
            FROM incidentcompass.triage_jobs AS job
            JOIN incidentcompass.faults AS fault ON fault.id = job.fault_id
            WHERE fault.service_name = @service_name;
            """,
            serviceName);

    private static async Task<long> ScalarAsync(string connectionString, string sql, string serviceName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("service_name", serviceName);
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return (long)result!;
    }

    private async Task<TestScope> CreateScopeAsync()
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IncidentCompass", connectionString);
            builder.UseSetting(
                "IncidentCompass:IngestionLimits:MaxSignalsPerExport",
                TestLimit.ToString(CultureInfo.InvariantCulture));
            builder.UseExplicitMockProviders();
        });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        return new TestScope(factory, client, connectionString);
    }

    private sealed record TestScope(
        WebApplicationFactory<Program> Factory,
        HttpClient Client,
        string ConnectionString) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }
}
