using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Domain.Incidents;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// Covers the half of the source and tickets worker paths that executing the governed tool alone
/// never reaches: <see cref="WorkerRoleRunner" />, where the role's configured
/// <c>OutputSchema</c> is evaluated by <c>AnalysisWorkerOutputSchemaValidator</c>. The scripted model
/// client builds the worker output from the real tool result by a copy rule hardcoded here, so this
/// test fails when a role schema regains a construct the validator rejects, and when a role schema
/// stops accepting the shape a real tool result takes under that rule. It reads no instruction file
/// and so cannot detect an instruction that contradicts its schema: the copy rule below is a
/// hand-maintained transcription of what those two roles' instructions say, and changing the
/// instructions means changing it here too.
/// </summary>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class WorkerRoleOutputSchemaPathTests(PostgresRepositoryFixture postgres)
{
    private const string ServiceName = "checkout";
    private const string Release = "2026.08.28.1";

    [DockerAvailableFact]
    public async Task MatchedToolResults_SatisfyEachRoleConfiguredOutputSchema()
    {
        var sourceRoot = Directory.CreateTempSubdirectory("ic-role-schema-matched-");
        try
        {
            var sourcePath = Path.Combine(sourceRoot.FullName, "src", "Checkout.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            await File.WriteAllTextAsync(
                sourcePath,
                string.Join('\n', Enumerable.Range(1, 30).Select(index => $"line {index}")),
                TestContext.Current.CancellationToken);
            using var scope = await CreateScopeAsync(sourceRoot.FullName, MatchedTicketSearch());
            var jobId = await IngestAsync(
                scope.Client,
                "role-output-matched-",
                $"   at Checkout.Run() in {sourcePath}:line 15");
            var claimed = await ClaimAsync(scope, jobId, "worker-role-schema-matched");

            using var source = await RunRoleAsync(claimed, "source");
            Assert.True(source.RootElement.GetProperty("matched").GetBoolean());
            var sourceItem = Assert.Single(source.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("src/Checkout.cs", sourceItem.GetProperty("relativePath").GetString());
            Assert.Equal(Release, sourceItem.GetProperty("release").GetString());
            Assert.Equal("heuristic", sourceItem.GetProperty("mappingMethod").GetString());
            Assert.Equal(JsonValueKind.Number, sourceItem.GetProperty("lineStart").ValueKind);
            Assert.False(source.RootElement.TryGetProperty("noMatchReason", out _));

            using var tickets = await RunRoleAsync(claimed, "tickets");
            Assert.True(tickets.RootElement.GetProperty("matched").GetBoolean());
            var ticketItem = Assert.Single(tickets.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("42", ticketItem.GetProperty("externalId").GetString());
            Assert.False(ticketItem.TryGetProperty("assignee", out _));
            Assert.False(tickets.RootElement.TryGetProperty("noMatchReason", out _));
        }
        finally
        {
            sourceRoot.Delete(recursive: true);
        }
    }

    [DockerAvailableFact]
    public async Task EmptyToolOutcomes_CarryNoMatchReasonThroughEachRoleOutputSchema()
    {
        var sourceRoot = Directory.CreateTempSubdirectory("ic-role-schema-empty-");
        try
        {
            using var scope = await CreateScopeAsync(
                sourceRoot.FullName,
                new StubTicketSearch(TicketSearchResult.NoMatch("github", "owner/repo")));
            var jobId = await IngestAsync(scope.Client, "role-output-empty-", stackTrace: null);
            var claimed = await ClaimAsync(scope, jobId, "worker-role-schema-empty");

            using var source = await RunRoleAsync(claimed, "source");
            Assert.False(source.RootElement.GetProperty("matched").GetBoolean());
            Assert.Empty(source.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("source_frames_not_found", source.RootElement.GetProperty("noMatchReason").GetString());

            using var tickets = await RunRoleAsync(claimed, "tickets");
            Assert.False(tickets.RootElement.GetProperty("matched").GetBoolean());
            Assert.Empty(tickets.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("ticket_search_no_matches", tickets.RootElement.GetProperty("noMatchReason").GetString());
        }
        finally
        {
            sourceRoot.Delete(recursive: true);
        }
    }

    private static async Task<JsonDocument> RunRoleAsync(ClaimedJob claimed, string roleName)
    {
        var role = claimed.Configuration.Roles[roleName];
        var validatedOutput = await claimed.Services.GetRequiredService<WorkerRoleRunner>().RunAsync(
            claimed.Job,
            claimed.Configuration,
            claimed.Investigation,
            roleName,
            role,
            "Return the bounded " + roleName + " context for this fault.",
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);
        return JsonDocument.Parse(validatedOutput);
    }

    private async Task<TestScope> CreateScopeAsync(string sourceRoot, ITicketSearch ticketSearch)
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
            builder.UseSetting("IncidentCompass:SourceContext:Roots:0:ServiceName", ServiceName);
            builder.UseSetting("IncidentCompass:SourceContext:Roots:0:Release", Release);
            builder.UseSetting("IncidentCompass:SourceContext:Roots:0:RootPath", sourceRoot);
            builder.UseSetting("IncidentCompass:Tickets:GitHub:Owner", "owner");
            builder.UseSetting("IncidentCompass:Tickets:GitHub:Repository", "repo");
            builder.UseSetting("IncidentCompass:Tickets:GitHub:Token", "host-secret");
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Scoped<ITicketSearch>(_ => ticketSearch));
                services.RemoveAll<IAiModelClient>();
                services.AddScoped<IAiModelClient>(_ => new RoleOutputModelClient());
            });
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        return new TestScope(factory, client);
    }

    private static async Task<ClaimedJob> ClaimAsync(TestScope scope, Guid jobId, string workerId)
    {
        var serviceScope = scope.Factory.Services.CreateScope();
        scope.ServiceScopes.Add(serviceScope);
        var services = serviceScope.ServiceProvider;
        var job = await services.GetRequiredService<ITriageJobRunner>().ClaimNextAsync(
            workerId,
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        Assert.NotNull(job);
        Assert.Equal(jobId, job.Id);
        var configuration = await services.GetRequiredService<ITriageConfigurationRepository>()
            .GetByHashAsync(job.ConfigHash, TestContext.Current.CancellationToken);
        var investigation = await services.GetRequiredService<ITriageJobInvestigationContextRepository>()
            .GetAsync(job.Id, job.Attempt, TestContext.Current.CancellationToken);
        return new ClaimedJob(job, services, configuration, investigation);
    }

    /// <summary>
    /// Every ingest has to open its own fault, so <paramref name="faultPrefix" /> must differ per
    /// test. Both tests share one database, and scope setup terminalizes the previous test's fault,
    /// so a repeated fingerprint either attaches to the still-open fault or falls inside the closed
    /// fault's silence window; both of those grouping outcomes return no job id. The fingerprint
    /// masks long hex runs out of the error message, so the per-ingest GUID does not separate two
    /// signals on its own and only the prefix survives into the fingerprint.
    /// </summary>
    private static async Task<Guid> IngestAsync(HttpClient client, string faultPrefix, string? stackTrace)
    {
        var unique = faultPrefix + Guid.NewGuid().ToString("N");
        var attributes = new Dictionary<string, object?>
        {
            ["errorType"] = "TimeoutException",
            ["errorMessage"] = unique
        };
        if (stackTrace is not null)
        {
            attributes["exception.stacktrace"] = stackTrace;
        }

        var response = await client.PostAsJsonAsync(
            "/api/v1/incidents",
            new
            {
                sourceKind = "otel",
                serviceName = ServiceName,
                environment = "prod",
                externalId = unique,
                observedAtUtc = DateTimeOffset.UtcNow,
                attributes
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ingested = await response.Content.ReadFromJsonAsync<IngestionSignalResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(ingested);
        Assert.NotNull(ingested.JobId);
        return ingested.JobId.Value;
    }

    private static StubTicketSearch MatchedTicketSearch() => new(new TicketSearchResult(
        TicketSearchOutcome.Matched,
        "ticket_search_matches",
        [
            new TicketSearchMatch(
                "github",
                "owner/repo",
                "42",
                "Checkout timeout",
                "open",
                // A null assignee is what the instructions' "omit a null tool field" rule bites on.
                null,
                DateTimeOffset.UnixEpoch,
                "https://example.invalid/owner/repo/issues/42",
                0.85)
        ],
        "github",
        "owner/repo"));

    /// <summary>
    /// Proposes the role's single granted tool once, then answers with the worker output built from
    /// that tool result by the copy rule the source and tickets role instructions state: only
    /// <c>matched</c>, <c>items</c> and a non-null <c>noMatchReason</c> at the top level, and no item
    /// field whose tool value was null.
    /// </summary>
    private sealed class RoleOutputModelClient : IAiModelClient
    {
        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            var toolResult = request.Messages.LastOrDefault(message => message.Role == AiMessageRole.Tool);
            if (toolResult is not null)
            {
                return Task.FromResult(Respond(request, BuildRoleOutput(toolResult.Content), []));
            }

            if (request.Tools is not { Count: 1 })
            {
                throw new InvalidOperationException(
                    "The worker tool surface must offer exactly the one tool its role is granted.");
            }

            var tool = request.Tools[0];
            return Task.FromResult(Respond(
                request,
                string.Empty,
                [new AiToolCall(tool.Name + "-call", tool.Name, tool.SchemaVersion, EmptyArguments())]));
        }

        private static string BuildRoleOutput(string toolResultJson)
        {
            var toolResult = JsonNode.Parse(toolResultJson)!.AsObject();
            Assert.True(
                toolResult["matched"] is not null && toolResult["items"] is JsonArray,
                "The tool message carries no 'matched'/'items' result, so the tool never produced one: " +
                "governance denied it or it failed, and the worker was handed the failure envelope " +
                $"instead. Tool message: {toolResultJson}");

            var items = new JsonArray();
            foreach (var item in toolResult["items"]!.AsArray())
            {
                var copied = new JsonObject();
                foreach (var field in item!.AsObject())
                {
                    if (field.Value is not null && field.Value.GetValueKind() != JsonValueKind.Null)
                    {
                        copied[field.Key] = field.Value.DeepClone();
                    }
                }

                items.Add(copied);
            }

            var output = new JsonObject
            {
                ["matched"] = toolResult["matched"]!.DeepClone(),
                ["items"] = items
            };
            if (toolResult["noMatchReason"] is { } noMatchReason &&
                noMatchReason.GetValueKind() == JsonValueKind.String)
            {
                output["noMatchReason"] = noMatchReason.DeepClone();
            }

            return output.ToJsonString();
        }

        private static AiModelResponse Respond(
            AiModelRequest request,
            string content,
            IReadOnlyList<AiToolCall> toolCalls) =>
            new(
                content,
                request.Model,
                "role-output-test",
                new AiModelUsage(10, 5, 15),
                request.CorrelationId,
                toolCalls);

        private static JsonElement EmptyArguments()
        {
            using var document = JsonDocument.Parse("{}");
            return document.RootElement.Clone();
        }
    }

    private sealed class StubTicketSearch(TicketSearchResult result) : ITicketSearch
    {
        public Task<TicketSearchResult> SearchAsync(TicketSearchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed record ClaimedJob(
        TriageJob Job,
        IServiceProvider Services,
        TriageConfiguration Configuration,
        TriageJobInvestigationContext Investigation);

    private sealed record TestScope(WebApplicationFactory<Program> Factory, HttpClient Client) : IDisposable
    {
        public List<IServiceScope> ServiceScopes { get; } = [];

        public void Dispose()
        {
            foreach (var serviceScope in ServiceScopes)
            {
                serviceScope.Dispose();
            }

            Client.Dispose();
            Factory.Dispose();
        }
    }
}
