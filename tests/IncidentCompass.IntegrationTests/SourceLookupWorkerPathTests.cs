using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class SourceLookupWorkerPathTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task OTelStackTrace_ExecutesGovernedSourceToolAndPublishesClosedCitation()
    {
        var sourceRoot = Directory.CreateTempSubdirectory("ic-source-worker-");
        try
        {
            var sourcePath = Path.Combine(sourceRoot.FullName, "src", "Checkout.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            await File.WriteAllTextAsync(
                sourcePath,
                string.Join('\n', Enumerable.Range(1, 30).Select(index => $"line {index}")),
                TestContext.Current.CancellationToken);
            using var scope = await CreateScopeAsync(sourceRoot.FullName);
            var ingested = await PostOtelSignalAsync(scope.Client, sourcePath);
            using var serviceScope = scope.Factory.Services.CreateScope();
            var runner = serviceScope.ServiceProvider.GetRequiredService<ITriageJobRunner>();
            var job = await runner.ClaimNextAsync(
                "worker-source-e2e",
                TimeSpan.FromMinutes(5),
                TestContext.Current.CancellationToken);
            Assert.NotNull(job);
            Assert.Equal(ingested.JobId, job.Id);

            var configRepository = serviceScope.ServiceProvider.GetRequiredService<ITriageConfigurationRepository>();
            var configuration = await configRepository.GetByHashAsync(job.ConfigHash, TestContext.Current.CancellationToken);
            var contextRepository = serviceScope.ServiceProvider.GetRequiredService<ITriageJobInvestigationContextRepository>();
            var investigation = await contextRepository.GetAsync(job.Id, job.Attempt, TestContext.Current.CancellationToken);
            var executor = serviceScope.ServiceProvider.GetRequiredService<WorkerToolCallExecutor>();
            var toolOutput = await executor.ExecuteAsync(
                job,
                configuration,
                investigation,
                "source",
                new AiToolCall("source-call", "source_lookup", "v1", Json("{}")),
                TestContext.Current.CancellationToken);
            using var output = JsonDocument.Parse(toolOutput);
            var item = Assert.Single(output.RootElement.GetProperty("items").EnumerateArray());
            var artifactId = item.GetProperty("artifactId").GetString()!;

            var publisher = serviceScope.ServiceProvider.GetRequiredService<TriageReportPublisher>();
            await publisher.PublishAsync(
                job,
                "worker-source-e2e",
                new AiToolCall("publish-source", "publish_report", "v1", PublishArguments(artifactId)),
                TestContext.Current.CancellationToken);

            var row = await ReadEvidenceAsync(scope.ConnectionString, ingested.FaultId);
            Assert.Equal("RetrievedItem", row.Kind);
            Assert.Equal("SourceCode", row.EvidenceKind);
            Assert.Equal("src/Checkout.cs", row.RelativePath);
            Assert.Equal("2026.08.28.1", row.Release);
            Assert.Equal("heuristic", row.MappingMethod);
        }
        finally
        {
            sourceRoot.Delete(recursive: true);
        }
    }

    private async Task<TestScope> CreateScopeAsync(string sourceRoot)
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        await PostgresTriageJobTestIsolation.CompleteClaimableJobsAsync(connectionString);
        var configPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "test-triage-config",
            "incidentcompass.config.json");
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IncidentCompass", connectionString);
            builder.UseExplicitMockProviders();
            builder.UseSetting("IncidentCompass:ConfigSource:Path", configPath);
            builder.UseSetting("IncidentCompass:SourceContext:Roots:0:ServiceName", "checkout");
            builder.UseSetting("IncidentCompass:SourceContext:Roots:0:Release", "2026.08.28.1");
            builder.UseSetting("IncidentCompass:SourceContext:Roots:0:RootPath", sourceRoot);
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        return new TestScope(factory, client, connectionString);
    }

    private static async Task<IngestResponse> PostOtelSignalAsync(HttpClient client, string sourcePath)
    {
        // The service name is fixed because the SourceContext root above is bound to it, so the
        // signal's fingerprint is made distinct through the error message instead.
        var unique = IngestFingerprintUniqueness.Token();
        var response = await client.PostAsJsonAsync(
            "/api/v1/incidents",
            new
            {
                sourceKind = "otel",
                serviceName = "checkout",
                environment = "prod",
                externalId = "source-" + unique,
                observedAtUtc = DateTimeOffset.UtcNow,
                attributes = new Dictionary<string, object?>
                {
                    ["errorType"] = "ExampleException",
                    ["errorMessage"] = "checkout source failure " + unique,
                    ["exception.stacktrace"] = $"   at Checkout.Run() in {sourcePath}:line 15"
                }
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IngestResponse>(TestContext.Current.CancellationToken))!;
    }

    private static JsonElement PublishArguments(string artifactId) => JsonSerializer.SerializeToElement(new
    {
        report_json = new
        {
            status = "Completed",
            summary = "Source-grounded report.",
            classification = "SimpleKnownError",
            confidence = "Medium",
            documentationFit = "Missing",
            evidence = new[] { new { referenceId = artifactId, quote = "line 15" } },
            limitations = Array.Empty<string>(),
            recommendedNextAction = "Review the cited source."
        }
    });

    private static async Task<EvidenceRow> ReadEvidenceAsync(string connectionString, Guid faultId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT e.kind,
                   a.redacted_payload->>'evidenceKind',
                   a.redacted_payload->>'relativePath',
                   a.redacted_payload->>'release',
                   a.redacted_payload->>'mappingMethod'
            FROM incidentcompass.triage_evidence e
            JOIN incidentcompass.triage_artifacts a ON a.id = e.artifact_id
            JOIN incidentcompass.triage_reports r ON r.id = e.report_id
            WHERE r.fault_id = @fault_id;
            """, connection);
        command.Parameters.AddWithValue("fault_id", faultId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return new EvidenceRow(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed record IngestResponse(Guid FaultId, Guid? JobId);

    private sealed record EvidenceRow(
        string Kind,
        string EvidenceKind,
        string RelativePath,
        string Release,
        string MappingMethod);

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
