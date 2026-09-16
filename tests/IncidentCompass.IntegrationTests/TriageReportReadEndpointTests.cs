using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Application.Investigation.Reports.List;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class TriageReportReadEndpointTests(PostgresRepositoryFixture postgres)
{
    private static readonly JsonSerializerOptions ResponseDeserializationOptions = new(JsonSerializerDefaults.Web);

    [DockerAvailableFact]
    public async Task GetTriageReportById_CitedPriorReportShowsUntrustedMarker()
    {
        using var scope = await CreateScopeAsync();
        var serviceName = "prior-report-svc-" + Guid.NewGuid().ToString("N");
        var first = await PostIngestAsync(scope.Client, serviceName);
        await RunClaimedJobAsync(scope, first.JobId!.Value, "worker-prior-1");
        await ExecuteAsync(scope.ConnectionString, """
            UPDATE incidentcompass.faults
            SET created_at_utc = now() - interval '32 minutes',
                completed_at_utc = now() - interval '31 minutes'
            WHERE id = @fault_id;
            """, ("fault_id", first.FaultId));

        var recurrence = await PostIngestAsync(scope.Client, serviceName);
        await RunClaimedJobAsync(scope, recurrence.JobId!.Value, "worker-prior-2");

        var reportId = await ScalarAsync<Guid>(scope.ConnectionString, "SELECT id FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;", ("fault_id", recurrence.FaultId));
        var response = await scope.Client.GetAsync("/api/v1/triage-reports/" + reportId, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TriageReportDetailsDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);

        var priorEvidence = Assert.Single(body.Evidence, evidence => evidence.Kind == "PriorReport");
        Assert.Equal("PriorReport", priorEvidence.ArtifactKind);
        Assert.Equal("untrusted-prior-hypothesis", priorEvidence.ArtifactPayload.GetProperty("trust").GetString());
    }

    [DockerAvailableFact]
    public async Task GetTriageReportById_CitedRecurrenceStateShowsEscalationFacts()
    {
        using var scope = await CreateScopeAsync(citeRecurrenceState: true);
        var serviceName = "recurrence-evidence-svc-" + Guid.NewGuid().ToString("N");
        var first = await PostIngestAsync(scope.Client, serviceName);
        await RunClaimedJobAsync(scope, first.JobId!.Value, "worker-recurrence-evidence-prior");
        await ExecuteAsync(scope.ConnectionString, "UPDATE incidentcompass.faults SET created_at_utc = now() - interval '3 hours', completed_at_utc = now() - interval '2 hours' WHERE id = @fault_id;", ("fault_id", first.FaultId));

        var recurrence = await PostIngestAsync(scope.Client, serviceName);
        await RunClaimedJobAsync(scope, recurrence.JobId!.Value, "worker-recurrence-evidence");
        Assert.Equal("Succeeded", await ScalarAsync<string>(scope.ConnectionString, "SELECT status FROM incidentcompass.triage_jobs WHERE id = @job_id;", ("job_id", recurrence.JobId!.Value)));

        var reportId = await ScalarAsync<Guid>(scope.ConnectionString, "SELECT id FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;", ("fault_id", recurrence.FaultId));
        var response = await scope.Client.GetAsync("/api/v1/triage-reports/" + reportId, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<TriageReportDetailsDto>(TestContext.Current.CancellationToken);

        Assert.NotNull(body);
        var recurrenceEvidence = Assert.Single(body.Evidence, evidence => evidence.Kind == "RecurrenceState");
        Assert.Equal("RecurrenceState", recurrenceEvidence.ArtifactKind);
        Assert.Equal(1, recurrenceEvidence.ArtifactPayload.GetProperty("recurrenceCount").GetInt32());
        Assert.False(recurrenceEvidence.ArtifactPayload.GetProperty("escalationIntentCreated").GetBoolean());
    }
    [DockerAvailableFact]
    public async Task RecurrenceEscalation_SchedulesReTriageAndSupersedesPriorReport()
    {
        using var scope = await CreateScopeAsync(useReTriageConfig: true);
        var serviceName = "retriage-svc-" + Guid.NewGuid().ToString("N");
        var first = await PostIngestAsync(scope.Client, serviceName);
        await RunClaimedJobAsync(scope, first.JobId!.Value, "worker-retriage-prior");

        var firstReportId = await ScalarAsync<Guid>(
            scope.ConnectionString,
            "SELECT id FROM incidentcompass.triage_reports WHERE job_id = @job_id;",
            ("job_id", first.JobId!.Value));
        await ExecuteAsync(scope.ConnectionString, "UPDATE incidentcompass.faults SET created_at_utc = now() - interval '3 hours', completed_at_utc = now() - interval '2 hours' WHERE id = @fault_id;", ("fault_id", first.FaultId));

        var recurrence = await PostIngestAsync(scope.Client, serviceName);
        var reTriageJobId = await ScalarAsync<Guid>(
            scope.ConnectionString,
            "SELECT id FROM incidentcompass.triage_jobs WHERE retriage_trigger_job_id = @trigger_job_id;",
            ("trigger_job_id", recurrence.JobId!.Value));
        Assert.Equal(first.FaultId, await ScalarAsync<Guid>(scope.ConnectionString, "SELECT fault_id FROM incidentcompass.triage_jobs WHERE id = @job_id;", ("job_id", reTriageJobId)));
        Assert.Equal(firstReportId, await ScalarAsync<Guid>(scope.ConnectionString, "SELECT supersedes_report_id FROM incidentcompass.triage_jobs WHERE id = @job_id;", ("job_id", reTriageJobId)));
        Assert.Equal(1, await ScalarAsync<long>(scope.ConnectionString, "SELECT count(*) FROM incidentcompass.triage_artifacts WHERE job_id = @job_id AND kind = 'PriorReport';", ("job_id", reTriageJobId)));

        await RunClaimedJobAsync(scope, reTriageJobId, "worker-retriage-successor");

        var successorReportId = await ScalarAsync<Guid>(
            scope.ConnectionString,
            "SELECT id FROM incidentcompass.triage_reports WHERE job_id = @job_id;",
            ("job_id", reTriageJobId));
        Assert.Equal(firstReportId, await ScalarAsync<Guid>(scope.ConnectionString, "SELECT supersedes_report_id FROM incidentcompass.triage_reports WHERE id = @report_id;", ("report_id", successorReportId)));
        Assert.Equal("Completed", await ScalarAsync<string>(scope.ConnectionString, "SELECT status FROM incidentcompass.faults WHERE id = @fault_id;", ("fault_id", first.FaultId)));

        var prior = await GetReportAsync(scope.Client, "/api/v1/triage-reports/" + firstReportId);
        Assert.Equal(successorReportId, prior.SupersededByReportId);
        Assert.False(prior.IsLatestForFault);
        var successor = await GetReportAsync(scope.Client, "/api/v1/triage-reports/" + successorReportId);
        Assert.Equal("LikelyRegression", successor.Classification);
        Assert.Contains(successor.Evidence, evidence => evidence.Kind == "RecurrenceState");
        var latest = await GetReportAsync(scope.Client, "/api/v1/faults/" + first.FaultId + "/triage-report");
        Assert.Equal(successorReportId, latest.Id);
    }

    [DockerAvailableFact]
    public async Task StalledReTriage_EndsSucceededWithABackendReportCitingTheRecurrenceState()
    {
        // The first job publishes normally. The re-triage job's orchestrator only ever delegates the
        // same memory task and its worker only ever repeats the same search, so repetition, the worker
        // stop, one recovery and then termination run through the real runner and PostgreSQL.
        using var scope = await CreateScopeAsync(useReTriageConfig: true, stallReTriage: true);
        var serviceName = "retriage-stall-svc-" + Guid.NewGuid().ToString("N");
        var first = await PostIngestAsync(scope.Client, serviceName);
        await RunClaimedJobAsync(scope, first.JobId!.Value, "worker-retriage-stall-prior");
        await ExecuteAsync(scope.ConnectionString, "UPDATE incidentcompass.faults SET created_at_utc = now() - interval '3 hours', completed_at_utc = now() - interval '2 hours' WHERE id = @fault_id;", ("fault_id", first.FaultId));
        var recurrence = await PostIngestAsync(scope.Client, serviceName);
        var reTriageJobId = await ScalarAsync<Guid>(
            scope.ConnectionString,
            "SELECT id FROM incidentcompass.triage_jobs WHERE retriage_trigger_job_id = @trigger_job_id;",
            ("trigger_job_id", recurrence.JobId!.Value));

        await RunClaimedJobAsync(scope, reTriageJobId, "worker-retriage-stall-successor");

        Assert.Equal("Succeeded", await ScalarAsync<string>(scope.ConnectionString, "SELECT status FROM incidentcompass.triage_jobs WHERE id = @job_id;", ("job_id", reTriageJobId)));
        var reportId = await ScalarAsync<Guid>(scope.ConnectionString, "SELECT id FROM incidentcompass.triage_reports WHERE job_id = @job_id;", ("job_id", reTriageJobId));
        var report = await GetReportAsync(scope.Client, "/api/v1/triage-reports/" + reportId);
        Assert.Equal("InsufficientEvidence", await ScalarAsync<string>(scope.ConnectionString, "SELECT status FROM incidentcompass.triage_reports WHERE id = @report_id;", ("report_id", reportId)));
        Assert.Equal("Unknown", report.Classification);
        Assert.Equal(NoProgressTerminationReport.Summary, await ScalarAsync<string>(scope.ConnectionString, "SELECT summary FROM incidentcompass.triage_reports WHERE id = @report_id;", ("report_id", reportId)));
        Assert.Equal(2, report.Evidence.Count);
        Assert.Contains(report.Evidence, evidence => evidence.Kind == "RecurrenceState");
        Assert.Contains(report.Evidence, evidence => evidence.Kind == "TriggerSignal");
        var published = await ScalarAsync<string>(
            scope.ConnectionString,
            "SELECT rationale FROM incidentcompass.triage_ledger WHERE job_id = @job_id AND event_type = 'ReportPublished';",
            ("job_id", reTriageJobId));
        Assert.Equal(ReservedReportText.BackendAuthoredLedgerPrefix + NoProgressTerminationReport.Summary, published);
        Assert.Equal(1L, await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT count(*) FROM incidentcompass.triage_ledger WHERE job_id = @job_id AND event_type = 'BudgetEvent' AND rationale LIKE 'no_progress: terminated reason=%';",
            ("job_id", reTriageJobId)));
    }

    [DockerAvailableFact]
    public async Task GetTriageReportHistory_ExposesImmutableSupersessionAndLatestFaultReport()
    {
        using var scope = await CreateScopeAsync();
        var signal = await PostIngestAsync(scope.Client, "report-history-svc-" + Guid.NewGuid().ToString("N"));
        await RunClaimedJobAsync(scope, signal.JobId!.Value, "worker-report-history");

        var firstReportId = await ScalarAsync<Guid>(
            scope.ConnectionString,
            "SELECT id FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;",
            ("fault_id", signal.FaultId));
        var otherFault = await PostIngestAsync(scope.Client, "report-history-other-svc-" + Guid.NewGuid().ToString("N"));
        var crossFaultException = await Record.ExceptionAsync(() => ExecuteAsync(scope.ConnectionString, """
            INSERT INTO incidentcompass.triage_reports (
                id, job_id, fault_id, supersedes_report_id, status, summary, classification, confidence,
                documentation_fit, limitations, config_hash, created_at_utc)
            VALUES (
                @report_id, @job_id, @fault_id, @supersedes_report_id, 'Completed', 'Invalid cross-fault report.',
                'LikelyRegression', 'High', 'Missing', ARRAY[]::text[], @config_hash, now());
            """, ("report_id", Guid.NewGuid()), ("job_id", otherFault.JobId!.Value), ("fault_id", otherFault.FaultId),
            ("supersedes_report_id", firstReportId), ("config_hash", otherFault.ConfigHash!)));
        Assert.Equal("23503", Assert.IsType<PostgresException>(crossFaultException).SqlState);

        var successorJobId = Guid.NewGuid();
        await ExecuteAsync(scope.ConnectionString, """
            INSERT INTO incidentcompass.triage_jobs (
                id, fault_id, status, attempt, config_hash, created_at_utc, updated_at_utc)
            SELECT @job_id, fault_id, 'Succeeded', 1, config_hash, now(), now()
            FROM incidentcompass.triage_reports
            WHERE id = @first_id;
            """, ("job_id", successorJobId), ("first_id", firstReportId));
        var successorReportId = Guid.NewGuid();
        await ExecuteAsync(scope.ConnectionString, """
            INSERT INTO incidentcompass.triage_reports (
                id, job_id, fault_id, supersedes_report_id, status, summary, classification, confidence,
                documentation_fit, limitations, config_hash, created_at_utc)
            SELECT @successor_id, @job_id, fault_id, id, 'Completed', 'Re-triaged report.', 'LikelyRegression', 'High',
                   'Missing', ARRAY[]::text[], config_hash, created_at_utc + interval '1 second'
            FROM incidentcompass.triage_reports
            WHERE id = @first_id;
            """, ("successor_id", successorReportId), ("job_id", successorJobId), ("first_id", firstReportId));

        var first = await GetReportAsync(scope.Client, "/api/v1/triage-reports/" + firstReportId);
        Assert.Equal(successorReportId, first.SupersededByReportId);
        Assert.Null(first.SupersedesReportId);
        Assert.False(first.IsLatestForFault);

        var successor = await GetReportAsync(scope.Client, "/api/v1/triage-reports/" + successorReportId);
        Assert.Equal(firstReportId, successor.SupersedesReportId);
        Assert.Null(successor.SupersededByReportId);
        Assert.True(successor.IsLatestForFault);
        Assert.Equal("LikelyRegression", successor.Classification);

        using (var readScope = scope.Factory.Services.CreateScope())
        {
            var repository = readScope.ServiceProvider.GetRequiredService<ITriageReportReadRepository>();
            var directLatest = await repository.FindLatestByFaultIdAsync(signal.FaultId, "local", TestContext.Current.CancellationToken);
            Assert.NotNull(directLatest);
            Assert.Equal(successorReportId, directLatest.Id);
        }
        var latest = await GetReportAsync(scope.Client, "/api/v1/faults/" + signal.FaultId + "/triage-report");
        Assert.Equal(successorReportId, latest.Id);
        Assert.True(latest.IsLatestForFault);
    }
    [DockerAvailableFact]
    public async Task TenantScopedReadPaths_HideForeignDataAndListUsesKeysetPagination()
    {
        using var scope = await CreateScopeAsync();
        var sharedService = "tenant-scope-svc-" + Guid.NewGuid().ToString("N");
        var local = await PostIngestAsync(scope.Client, sharedService);
        await RunClaimedJobAsync(scope, local.JobId!.Value, "worker-tenant-local");
        var foreign = await PostIngestAsync(scope.Client, sharedService, "ForeignException");
        await RunClaimedJobAsync(scope, foreign.JobId!.Value, "worker-tenant-foreign");
        var foreignReportId = await ScalarAsync<Guid>(
            scope.ConnectionString,
            "SELECT id FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;",
            ("fault_id", foreign.FaultId));
        await ExecuteAsync(
            scope.ConnectionString,
            "UPDATE incidentcompass.faults SET tenant_id = 'other-tenant' WHERE id = @fault_id;",
            ("fault_id", foreign.FaultId));

        foreach (var path in new[]
        {
            "/api/v1/triage-reports/" + foreignReportId,
            "/api/v1/faults/" + foreign.FaultId + "/triage-report",
            "/api/v1/faults/" + foreign.FaultId,
            "/api/v1/faults/" + foreign.FaultId + "/ledger"
        })
        {
            var response = await scope.Client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using (var readScope = scope.Factory.Services.CreateScope())
        {
            var repository = readScope.ServiceProvider.GetRequiredService<ITriageReportListRepository>();
            var direct = await repository.ListAsync(
                new TriageReportListFilter(null, sharedService, null, null, null, null, null, 10),
                "local",
                TestContext.Current.CancellationToken);
            Assert.Single(direct);
        }
        var filtered = await GetReportListAsync(scope.Client, $"/api/v1/triage-reports?serviceName={sharedService}&limit=10");
        var localReportId = await ScalarAsync<Guid>(
            scope.ConnectionString,
            "SELECT id FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;",
            ("fault_id", local.FaultId));
        Assert.Equal([localReportId], filtered.Reports.Select(report => report.Id));
        Assert.Null(filtered.NextCursor);

        var keysetService = "keyset-svc-" + Guid.NewGuid().ToString("N");
        for (var index = 0; index < 3; index++)
        {
            var signal = await PostIngestAsync(scope.Client, keysetService, "KeysetException" + index);
            await RunClaimedJobAsync(scope, signal.JobId!.Value, "worker-keyset-" + index);
        }

        var firstPage = await GetReportListAsync(scope.Client, $"/api/v1/triage-reports?serviceName={keysetService}&limit=1");
        Assert.Single(firstPage.Reports);
        Assert.NotNull(firstPage.NextCursor);
        var secondPage = await GetReportListAsync(scope.Client, $"/api/v1/triage-reports?serviceName={keysetService}&limit=1&cursor={firstPage.NextCursor}");
        Assert.Single(secondPage.Reports);
        Assert.NotEqual(firstPage.Reports[0].Id, secondPage.Reports[0].Id);
        var filterResponse = await GetReportListAsync(
            scope.Client,
            $"/api/v1/triage-reports?faultId={local.FaultId}&environment=prod&status=Completed&classification=SimpleKnownError&limit=10");
        Assert.Equal([localReportId], filterResponse.Reports.Select(report => report.Id));
        Assert.Equal(HttpStatusCode.BadRequest, (await scope.Client.GetAsync("/api/v1/triage-reports?limit=101", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await scope.Client.GetAsync("/api/v1/triage-reports?cursor=invalid", TestContext.Current.CancellationToken)).StatusCode);
        var malformedCursorBytes = new byte[24];
        Array.Fill(malformedCursorBytes, byte.MaxValue);
        var malformedCursor = Convert.ToBase64String(malformedCursorBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(HttpStatusCode.BadRequest, (await scope.Client.GetAsync("/api/v1/triage-reports?cursor=" + malformedCursor, TestContext.Current.CancellationToken)).StatusCode);
    }

    private static async Task<TriageReportListDto> GetReportListAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"Expected a successful report list response for '{path}', received {(int)response.StatusCode}: {content}");
        using var document = JsonDocument.Parse(content);
        Assert.All(document.RootElement.GetProperty("reports").EnumerateArray(), report => Assert.False(report.TryGetProperty("evidence", out _)));
        return JsonSerializer.Deserialize<TriageReportListDto>(content, ResponseDeserializationOptions)!;
    }
    private async Task<TestScope> CreateScopeAsync(bool citeRecurrenceState = false, bool useReTriageConfig = false, bool stallReTriage = false)
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        await PostgresTriageJobTestIsolation.CompleteClaimableJobsAsync(connectionString);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IncidentCompass", connectionString);
            builder.UseExplicitMockProviders();
            if (useReTriageConfig)
            {
                builder.UseSetting("IncidentCompass:ConfigSource:Path", ReTriageFixtureConfigPath());
            }

            if (stallReTriage)
            {
                builder.UseWorkerModelHost();
            }
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAiModelClient>();
                services.AddScoped<IAiModelClient>(_ => stallReTriage
                    ? new StallingReTriageModelClient()
                    : useReTriageConfig
                    ? new ReTriageModelClient()
                    : citeRecurrenceState
                        ? new RecurrenceStateCitingModelClient()
                        : new PriorReportCitingModelClient());
            });
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        return new TestScope(factory, client, connectionString);
    }

    private static string ReTriageFixtureConfigPath() =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "retriage-triage-config", "incidentcompass.config.json");

    private static async Task RunClaimedJobAsync(TestScope scope, Guid expectedJobId, string workerId)
    {
        await ExecuteAsync(scope.ConnectionString, """
            UPDATE incidentcompass.triage_jobs
            SET status = 'DeadLettered', locked_by = NULL, locked_until_utc = NULL
            WHERE id <> @job_id AND status = 'Pending';
            """, ("job_id", expectedJobId));

        using var serviceScope = scope.Factory.Services.CreateScope();
        var runner = serviceScope.ServiceProvider.GetRequiredService<ITriageJobRunner>();
        var claimed = await runner.ClaimNextAsync(workerId, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        Assert.NotNull(claimed);
        Assert.Equal(expectedJobId, claimed.Id);
        await runner.ProcessClaimedAsync(
            claimed,
            workerId,
            new TriageJobProcessingSettings(MaxAttempts: 1, RetryDelay: TimeSpan.FromSeconds(1)),
            TestContext.Current.CancellationToken);
    }

    private static async Task<IngestSignalResponseDto> PostIngestAsync(HttpClient client, string serviceName, string errorType = "TimeoutException")
    {
        var unique = Guid.NewGuid().ToString("N");
        var response = await client.PostAsJsonAsync(
            "/api/v1/incidents",
            new TesterEnvelopeDto(
                "tester",
                serviceName,
                "prod",
                DateTimeOffset.UtcNow,
                new TesterAttributesDto(errorType, "prior report timeout " + unique, "/prior")),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestSignalResponseDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.NotNull(body.JobId);
        return body;
    }

    private static async Task<TriageReportDetailsDto> GetReportAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"Expected a successful report response for '{path}', received {(int)response.StatusCode}: {content}");
        return JsonSerializer.Deserialize<TriageReportDetailsDto>(content, ResponseDeserializationOptions)!;
    }
    private static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }

    private sealed class PriorReportCitingModelClient : IAiModelClient
    {
        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            var prior = TryFindPromptArtifactId(request, "PriorReport");
            var referenceId = prior ?? TryFindPromptArtifactId(request, "TriggerSignal") ?? throw new InvalidOperationException("No citable prompt artifact was found.");
            return Task.FromResult(Response(request, [PublishCall(referenceId)]));
        }

        private static AiModelResponse Response(AiModelRequest request, IReadOnlyList<AiToolCall> toolCalls)
        {
            return new AiModelResponse("publish", request.Model, "prior-report-test", new AiModelUsage(10, 5, 15), request.CorrelationId, toolCalls);
        }

        private static AiToolCall PublishCall(string referenceId)
        {
            using var arguments = JsonDocument.Parse("{\"report_json\":{\"status\":\"Completed\",\"summary\":\"Prior report citation.\",\"classification\":\"SimpleKnownError\",\"confidence\":\"Medium\",\"documentationFit\":\"Missing\",\"evidence\":[{\"referenceId\":\"" + referenceId + "\"}],\"limitations\":[],\"recommendedNextAction\":\"Review cited prior context.\"}}");
            return new AiToolCall("publish-prior", "publish_report", "v1", arguments.RootElement.Clone());
        }

        private static string? TryFindPromptArtifactId(AiModelRequest request, string kind)
        {
            var prompt = request.Messages.First(static message => message.Role == AiMessageRole.User).Content;
            foreach (var line in prompt.Split('\n'))
            {
                if (!line.Contains("kind=" + kind, StringComparison.Ordinal))
                {
                    continue;
                }

                var marker = "artifact:";
                var start = line.IndexOf(marker, StringComparison.Ordinal);
                return line[(start + marker.Length)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            }

            return null;
        }
    }

    private sealed class RecurrenceStateCitingModelClient : IAiModelClient
    {
        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            var referenceId = TryFindPromptArtifactId(request, "RecurrenceState")
                ?? TryFindPromptArtifactId(request, "TriggerSignal")
                ?? throw new InvalidOperationException("No citable artifact was found.");
            using var arguments = JsonDocument.Parse("{\"report_json\":{\"status\":\"Completed\",\"summary\":\"Recurrence state citation.\",\"classification\":\"SimpleKnownError\",\"confidence\":\"Medium\",\"documentationFit\":\"Missing\",\"evidence\":[{\"referenceId\":\"" + referenceId + "\"}],\"limitations\":[],\"recommendedNextAction\":\"Review recurrence facts.\"}}");
            return Task.FromResult(new AiModelResponse(
                "publish", request.Model, "recurrence-state-test", new AiModelUsage(10, 5, 15), request.CorrelationId,
                [new AiToolCall("publish-recurrence", "publish_report", "v1", arguments.RootElement.Clone())]));
        }

        private static string? TryFindPromptArtifactId(AiModelRequest request, string kind)
        {
            var prompt = request.Messages.First(static message => message.Role == AiMessageRole.User).Content;
            var line = prompt.Split('\n').FirstOrDefault(line => line.Contains("kind=" + kind, StringComparison.Ordinal));
            if (line is null)
            {
                return null;
            }

            var start = line.IndexOf("artifact:", StringComparison.Ordinal);
            return line[(start + "artifact:".Length)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        }
    }
    private sealed class ReTriageModelClient : IAiModelClient
    {
        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            var recurrence = FindArtifactId(request, "RecurrenceState");
            var hasPriorReport = FindArtifactId(request, "PriorReport") is not null;
            var referenceId = recurrence ?? FindArtifactId(request, "TriggerSignal")
                ?? throw new InvalidOperationException("No citable prompt artifact was found.");
            var classification = hasPriorReport ? "LikelyRegression" : "SimpleKnownError";
            var summary = hasPriorReport ? "Recurrence escalated re-triage." : "Initial triage report.";
            using var arguments = JsonDocument.Parse("{\"report_json\":{\"status\":\"Completed\",\"summary\":\"" + summary + "\",\"classification\":\"" + classification + "\",\"confidence\":\"Medium\",\"documentationFit\":\"Missing\",\"evidence\":[{\"referenceId\":\"" + referenceId + "\"}],\"limitations\":[],\"recommendedNextAction\":\"Review recurrence facts.\"}}");
            return Task.FromResult(new AiModelResponse(
                "publish", request.Model, "retriage-test", new AiModelUsage(10, 5, 15), request.CorrelationId,
                [new AiToolCall("publish-retriage", "publish_report", "v1", arguments.RootElement.Clone())]));
        }

        private static string? FindArtifactId(AiModelRequest request, string kind)
        {
            var prompt = request.Messages.First(message => message.Role == AiMessageRole.User).Content;
            var line = prompt.Split('\n').FirstOrDefault(value => value.Contains("kind=" + kind, StringComparison.Ordinal));
            if (line is null)
            {
                return null;
            }

            var start = line.IndexOf("artifact:", StringComparison.Ordinal);
            return line[(start + "artifact:".Length)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        }
    }

    /// <summary>
    /// Publishes the first triage like <see cref="ReTriageModelClient"/>; on the re-triage job, whose
    /// prompt carries a prior report, it only delegates one memory task, only repeats one search and
    /// answers a recovery call with plain text.
    /// </summary>
    private sealed class StallingReTriageModelClient : IAiModelClient
    {
        private readonly ReTriageModelClient publisher = new();

        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            var toolNames = request.Tools?.Select(static tool => tool.Name).ToHashSet(StringComparer.Ordinal) ?? [];
            var prompt = request.Messages.First(message => message.Role == AiMessageRole.User).Content;
            if (toolNames.Contains("memory_search"))
            {
                return Respond(request, "search", Call("memory_search", "{\"query\":\"checkout timeout inventory\"}"));
            }

            if (toolNames.Contains("delegate") && prompt.Contains("kind=PriorReport", StringComparison.Ordinal))
            {
                return Respond(request, "delegate memory", Call("delegate", "{\"role\":\"memory\",\"task\":\"repeat memory_search\"}"));
            }

            return request.Tools is null
                ? Respond(request, "Delegate a narrower task to a different role.")
                : publisher.CompleteAsync(request, cancellationToken);
        }

        private static Task<AiModelResponse> Respond(AiModelRequest request, string content, params AiToolCall[] toolCalls) =>
            Task.FromResult(new AiModelResponse(
                content, request.Model, "retriage-stall-test", new AiModelUsage(10, 5, 15), request.CorrelationId, toolCalls));

        private static AiToolCall Call(string name, string argumentsJson)
        {
            using var arguments = JsonDocument.Parse(argumentsJson);
            return new AiToolCall(name + "-call", name, "v1", arguments.RootElement.Clone());
        }
    }

    private sealed record TestScope(WebApplicationFactory<Program> Factory, HttpClient Client, string ConnectionString) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }

    private sealed record TesterAttributesDto(string ErrorType, string ErrorMessage, string HttpRoute);

    private sealed record TesterEnvelopeDto(
        string SourceKind,
        string ServiceName,
        string Environment,
        DateTimeOffset ObservedAtUtc,
        TesterAttributesDto Attributes);

    private sealed record IngestSignalResponseDto(
        Guid SignalId,
        Guid FaultId,
        bool IsNewFault,
        bool IsNewJob,
        bool IsSuppressed,
        Guid? JobId,
        string? ConfigHash);

    private sealed record TriageReportListDto(IReadOnlyList<TriageReportListItemDto> Reports, string? NextCursor);

    private sealed record TriageReportListItemDto(Guid Id);
    private sealed record TriageReportDetailsDto(
        Guid Id,
        string Classification,
        Guid? SupersedesReportId,
        Guid? SupersededByReportId,
        bool IsLatestForFault,
        IReadOnlyList<TriageEvidenceDto> Evidence);

    private sealed record TriageEvidenceDto(
        string Kind,
        string ArtifactKind,
        JsonElement ArtifactPayload);
}

