using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Memory;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Infrastructure.ModelGateway.Mock;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// The column is called <c>redacted_payload</c>, so a secret a connector hands back must not survive
/// into it, and must not come back out of it into a later prompt. Each test seeds one connector with
/// a credential, runs the governed worker tool path, then finishes the job so the orchestrator builds
/// real model requests from the stored artifacts.
/// </summary>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class ToolArtifactRedactionWorkerPathTests(PostgresRepositoryFixture postgres)
{
    // Shaped to match the built-in AWS access-key rule: AKIA plus sixteen upper-case characters.
    private const string SeededSecret = "AKIA" + "REDACTIONE2EPROO";

    [DockerAvailableFact]
    public async Task SourceLookup_SeededSecretReachesNeitherTheArtifactNorALaterModelRequest()
    {
        var sourceRoot = Directory.CreateTempSubdirectory("ic-redaction-source-");
        try
        {
            var sourcePath = Path.Combine(sourceRoot.FullName, "src", "Checkout.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            await File.WriteAllTextAsync(
                sourcePath,
                string.Join(
                    '\n',
                    Enumerable.Range(1, 10).Select(index => "line " + index.ToString(CultureInfo.InvariantCulture))
                        .Concat(["private const string LegacyKey = \"" + SeededSecret + "\";"])
                        .Concat(Enumerable.Range(12, 10).Select(index => "line " + index.ToString(CultureInfo.InvariantCulture)))),
                TestContext.Current.CancellationToken);

            var observed = await RunToolThenFinishJobAsync(
                builder =>
                {
                    builder.UseSetting("IncidentCompass:SourceContext:Roots:0:ServiceName", "checkout");
                    builder.UseSetting("IncidentCompass:SourceContext:Roots:0:Release", "2026.08.28.1");
                    builder.UseSetting("IncidentCompass:SourceContext:Roots:0:RootPath", sourceRoot.FullName);
                },
                configureServices: null,
                stackTracePath: sourcePath,
                roleName: "source",
                toolName: "source_lookup",
                argumentsJson: "{}");

            AssertSecretIsAbsentEverywhere(observed);
            Assert.Contains("src/Checkout.cs", observed.ToolOutput, StringComparison.Ordinal);
            // The credential is gone but the code around it is not: that is the grounding the report
            // is built from, and losing it would be a worse outcome than the leak it prevents.
            Assert.Contains("private const string LegacyKey", observed.ArtifactPayloads, StringComparison.Ordinal);
            Assert.Contains("line 10", observed.ArtifactPayloads, StringComparison.Ordinal);
        }
        finally
        {
            sourceRoot.Delete(recursive: true);
        }
    }

    [DockerAvailableFact]
    public async Task TicketSearch_SeededSecretReachesNeitherTheArtifactNorALaterModelRequest()
    {
        var match = new TicketSearchMatch(
            "github",
            "owner/repo",
            "42",
            "Rotate " + SeededSecret + " after the checkout regression",
            "open",
            "octocat",
            DateTimeOffset.Parse("2026-01-15T00:00:00Z", CultureInfo.InvariantCulture),
            "https://github.example/owner/repo/issues/42",
            0.85);

        var observed = await RunToolThenFinishJobAsync(
            configureHost: null,
            configureServices: services => services.Replace(ServiceDescriptor.Scoped<ITicketSearch>(
                _ => new StubTicketSearch(new TicketSearchResult(
                    TicketSearchOutcome.Matched, "ticket_search_matches", [match], "github", "owner/repo")))),
            stackTracePath: null,
            roleName: "tickets",
            toolName: "ticket_search",
            argumentsJson: "{}");

        AssertSecretIsAbsentEverywhere(observed);
        Assert.Contains("owner/repo", observed.ArtifactPayloads, StringComparison.Ordinal);
        Assert.Contains("after the checkout regression", observed.ArtifactPayloads, StringComparison.Ordinal);
    }

    [DockerAvailableFact]
    public async Task MemorySearch_SeededSecretReachesNeitherTheArtifactNorALaterModelRequest()
    {
        // The reranker drops a candidate whose chunk text covers fewer than half the query tokens, so
        // the seeded chunk has to read like an answer to the query it is retrieved by. A chunk that
        // only shares the word "checkout" is filtered before any artifact exists, and the run would
        // then prove nothing about redaction.
        var match = new MemorySearchMatch(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "runbook",
            "runbooks/checkout.md",
            "Checkout recovery runbook",
            0,
            "Checkout recovery runbook: rotate the legacy credential " + SeededSecret +
            " and restart the checkout workers.",
            0.9,
            "checkout",
            null,
            "2026.08.28.1");

        var observed = await RunToolThenFinishJobAsync(
            configureHost: null,
            configureServices: services => services.Replace(
                ServiceDescriptor.Scoped<IMemoryRepository>(_ => new StubMemoryRepository([match]))),
            stackTracePath: null,
            roleName: "memory",
            toolName: "memory_search",
            argumentsJson: "{\"query\":\"checkout recovery runbook\"}");

        AssertSecretIsAbsentEverywhere(observed);
        Assert.Contains("\"matched\":true", observed.ToolOutput, StringComparison.Ordinal);
        Assert.Contains("Checkout recovery runbook", observed.ArtifactPayloads, StringComparison.Ordinal);
        // The rest of the runbook chunk survives the credential being removed from the middle of it.
        Assert.Contains("restart the checkout workers", observed.ArtifactPayloads, StringComparison.Ordinal);
    }

    private static void AssertSecretIsAbsentEverywhere(ObservedRun observed)
    {
        Assert.Contains("[REDACTED]", observed.ArtifactPayloads, StringComparison.Ordinal);
        Assert.DoesNotContain(SeededSecret, observed.ToolOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(SeededSecret, observed.ArtifactPayloads, StringComparison.Ordinal);
        Assert.NotEmpty(observed.ModelRequestText);
        Assert.DoesNotContain(SeededSecret, observed.ModelRequestText, StringComparison.Ordinal);
    }

    private async Task<ObservedRun> RunToolThenFinishJobAsync(
        Action<IWebHostBuilder>? configureHost,
        Action<IServiceCollection>? configureServices,
        string? stackTracePath,
        string roleName,
        string toolName,
        string argumentsJson)
    {
        var requests = new ConcurrentQueue<AiModelRequest>();
        using var scope = await CreateScopeAsync(requests, configureHost, configureServices);
        var ingested = await PostSignalAsync(scope.Client, stackTracePath);
        Assert.NotNull(ingested.JobId);

        string toolOutput;
        using (var serviceScope = scope.Factory.Services.CreateScope())
        {
            var runner = serviceScope.ServiceProvider.GetRequiredService<ITriageJobRunner>();
            var job = await runner.ClaimNextAsync(
                "worker-redaction", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
            Assert.NotNull(job);
            Assert.Equal(ingested.JobId, job.Id);

            var configuration = await serviceScope.ServiceProvider
                .GetRequiredService<ITriageConfigurationRepository>()
                .GetByHashAsync(job.ConfigHash, TestContext.Current.CancellationToken);
            var investigation = await serviceScope.ServiceProvider
                .GetRequiredService<ITriageJobInvestigationContextRepository>()
                .GetAsync(job.Id, job.Attempt, TestContext.Current.CancellationToken);
            toolOutput = await serviceScope.ServiceProvider
                .GetRequiredService<WorkerToolCallExecutor>()
                .ExecuteAsync(
                    job,
                    configuration,
                    investigation,
                    roleName,
                    new AiToolCall("redaction-call", toolName, "v1", Json(argumentsJson)),
                    TestContext.Current.CancellationToken);

            // Finishing the same claimed attempt is what turns the stored artifacts back into prompt
            // text: the orchestrator rereads every artifact of this attempt when it builds its request.
            await runner.ProcessClaimedAsync(
                job,
                "worker-redaction",
                new TriageJobProcessingSettings(1, TimeSpan.FromSeconds(1)),
                TestContext.Current.CancellationToken);
        }

        return new ObservedRun(
            toolOutput,
            await ReadArtifactPayloadsAsync(scope.ConnectionString, ingested.JobId.Value),
            string.Join(
                "\n",
                requests.SelectMany(request => request.Messages).Select(message => message.Content)));
    }

    private async Task<TestScope> CreateScopeAsync(
        ConcurrentQueue<AiModelRequest> requests,
        Action<IWebHostBuilder>? configureHost,
        Action<IServiceCollection>? configureServices)
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        await PostgresTriageJobTestIsolation.CompleteClaimableJobsAsync(connectionString);
        var configPath = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "test-triage-config", "incidentcompass.config.json");
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IncidentCompass", connectionString);
            builder.UseExplicitMockProviders();
            builder.UseSetting("IncidentCompass:ConfigSource:Path", configPath);
            configureHost?.Invoke(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAiModelClient>();
                services.AddScoped<IAiModelClient>(_ => new RecordingAiModelClient(
                    new MockAiModelClient(), requests));
                configureServices?.Invoke(services);
            });
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        return new TestScope(factory, client, connectionString);
    }

    private static async Task<IngestResponse> PostSignalAsync(HttpClient client, string? stackTracePath)
    {
        var unique = IngestFingerprintUniqueness.Token();
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["errorType"] = "ExampleException",
            ["errorMessage"] = "checkout redaction path " + unique,
            ["service.component"] = "checkout-api"
        };
        if (stackTracePath is not null)
        {
            attributes["exception.stacktrace"] = "   at Checkout.Run() in " + stackTracePath + ":line 11";
        }

        var response = await client.PostAsJsonAsync(
            "/api/v1/incidents",
            new
            {
                sourceKind = "otel",
                serviceName = "checkout",
                environment = "prod",
                externalId = "redaction-" + unique,
                observedAtUtc = DateTimeOffset.UtcNow,
                attributes
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IngestResponse>(TestContext.Current.CancellationToken))!;
    }

    private static async Task<string> ReadArtifactPayloadsAsync(string connectionString, Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT string_agg(redacted_payload::text, E'\n' ORDER BY created_at_utc, id)
            FROM incidentcompass.triage_artifacts
            WHERE job_id = @job_id;
            """, connection);
        command.Parameters.AddWithValue("job_id", jobId);
        return Assert.IsType<string>(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed record ObservedRun(string ToolOutput, string ArtifactPayloads, string ModelRequestText);

    private sealed record IngestResponse(Guid FaultId, Guid? JobId);

    private sealed class RecordingAiModelClient(
        IAiModelClient inner,
        ConcurrentQueue<AiModelRequest> requests) : IAiModelClient
    {
        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            requests.Enqueue(request);
            return inner.CompleteAsync(request, cancellationToken);
        }
    }

    private sealed class StubTicketSearch(TicketSearchResult result) : ITicketSearch
    {
        public Task<TicketSearchResult> SearchAsync(
            TicketSearchRequest request,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class StubMemoryRepository(IReadOnlyList<MemorySearchMatch> matches) : IMemoryRepository
    {
        public Task<IReadOnlyList<MemorySearchMatch>> SearchAsync(
            MemorySearchRequest request,
            CancellationToken cancellationToken) => Task.FromResult(matches);

        public Task<bool> SeedItemExistsAsync(
            string owner,
            MemorySeedItem item,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task ReconcileSeedCorpusAsync(
            MemorySeedCorpus corpus,
            CancellationToken cancellationToken) => Task.CompletedTask;
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
