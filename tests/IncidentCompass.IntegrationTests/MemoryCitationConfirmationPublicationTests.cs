using System.Collections.Concurrent;
using System.Text.Json;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Application.Memory;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Infrastructure.Investigation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using static IncidentCompass.IntegrationTests.TriageReportGroundingTestSupport;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// The publication transaction is the only point where the report's classification and the grounded
/// evidence set both exist, so it is where a <c>KnownIncident</c> report that rests only on memory
/// nothing confirmed is refused. These tests exercise that through the real repository and the real
/// grounder, because the band is read out of the stored artifact payload by the grounder's SQL.
/// </summary>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class MemoryCitationConfirmationPublicationTests(PostgresRepositoryFixture postgres)
{
    private const string TicketRepository = "incidentcompass-test/tickets";

    [DockerAvailableFact]
    public async Task PublishAsync_RefusesKnownIncidentCitingOnlyAnUnconfirmedMemoryDocument()
    {
        using var scope = await CreateScopeAsync(postgres);
        var ingested = await PostIngestAsync(scope.Client, "memory-confirmation-refused");
        var claimed = await ClaimAsync(scope, ingested.JobId!.Value, "worker-memory-confirmation-refused");
        var artifactId = await InsertRetrievedMemoryArtifactAsync(
            scope.ConnectionString, claimed.Id, claimed.Attempt, "Current", MemoryRetrievalConfidence.Low);

        var exception = await Assert.ThrowsAsync<TriageReportValidationException>(() => PublishAsync(
            scope,
            claimed,
            "worker-memory-confirmation-refused",
            "KnownIncident",
            [artifactId],
            DocumentationFitStatus.Current));

        Assert.Equal(MemoryCitationConfirmationRule.UnconfirmedMemoryCitationRefusal, exception.Message);
        Assert.Equal(0, await ReportCountAsync(scope.ConnectionString, ingested.FaultId));
    }

    [DockerAvailableFact]
    public async Task PublishAsync_AcceptsKnownIncidentWhenOneCitedMemoryDocumentIsConfirmed()
    {
        using var scope = await CreateScopeAsync(postgres);
        var ingested = await PostIngestAsync(scope.Client, "memory-confirmation-mixed");
        var claimed = await ClaimAsync(scope, ingested.JobId!.Value, "worker-memory-confirmation-mixed");
        var unconfirmed = await InsertRetrievedMemoryArtifactAsync(
            scope.ConnectionString, claimed.Id, claimed.Attempt, "Current", MemoryRetrievalConfidence.Low);
        var confirmed = await InsertRetrievedMemoryArtifactAsync(
            scope.ConnectionString, claimed.Id, claimed.Attempt, "Stale", MemoryRetrievalConfidence.Medium);

        await PublishAsync(
            scope,
            claimed,
            "worker-memory-confirmation-mixed",
            "KnownIncident",
            [unconfirmed, confirmed],
            DocumentationFitStatus.CurrentWithHistorical);

        Assert.Equal(1, await ReportCountAsync(scope.ConnectionString, ingested.FaultId));
    }

    /// <summary>
    /// The scoping, proved rather than read. A ticket-search result is stored under the same
    /// <c>RetrievedItem</c> artifact kind and reported under the same evidence kind as a memory
    /// document, and its closed payload shape cannot carry a band at all, so a rule phrased over the
    /// evidence kind would have refused this report.
    /// </summary>
    [DockerAvailableFact]
    public async Task PublishAsync_AcceptsKnownIncidentGroundedOnATicketSearchResult()
    {
        using var scope = await CreateScopeAsync(postgres, ConfigureTicketRepository);
        var ingested = await PostIngestAsync(scope.Client, "memory-confirmation-ticket");
        var claimed = await ClaimAsync(scope, ingested.JobId!.Value, "worker-memory-confirmation-ticket");
        var artifactId = await InsertTicketArtifactAsync(
            scope.ConnectionString, claimed.Id, claimed.Attempt, TicketRepository, 42);

        await PublishAsync(scope, claimed, "worker-memory-confirmation-ticket", "KnownIncident", artifactId);

        var evidence = await ReadSingleEvidenceAsync(scope.ConnectionString, ingested.FaultId);
        Assert.Equal("RetrievedItem", evidence.Kind);
    }

    /// <summary>The same for the other bandless closed payload shape.</summary>
    [DockerAvailableFact]
    public async Task PublishAsync_AcceptsKnownIncidentGroundedOnASourceLookupResult()
    {
        using var scope = await CreateScopeAsync(postgres);
        var serviceName = "memory-confirmation-source-" + Guid.NewGuid().ToString("N");
        var ingested = await PostIngestAsync(scope.Client, new TriageReportTesterEnvelope(
            "tester", serviceName, "prod", DateTimeOffset.UtcNow,
            new TriageReportTesterAttributes("ExampleException", "source citation", "/source")));
        var claimed = await ClaimAsync(scope, ingested.JobId!.Value, "worker-memory-confirmation-source");
        await SetCurrentReleaseAsync(scope.ConnectionString, claimed.ConfigHash, serviceName, "r1");
        var artifactId = await InsertSourceArtifactAsync(scope.ConnectionString, claimed.Id, claimed.Attempt, "r1");

        await PublishAsync(scope, claimed, "worker-memory-confirmation-source", "KnownIncident", artifactId);

        var evidence = await ReadSingleEvidenceAsync(scope.ConnectionString, ingested.FaultId);
        Assert.Equal("RetrievedItem", evidence.Kind);
    }

    /// <summary>
    /// The <c>memory_search</c> tool result is a citable artifact holding the same titles and quotes
    /// as that call's per-item artifacts, and it resolves to no memory item, so before this it was
    /// neither memory-backed for the rule nor a document for documentation fit: a
    /// <c>KnownIncident</c> could rest on exactly the retrieved text the rule exists to bound. No
    /// model is shown such an artifact id today, and the rule must not depend on that staying true.
    /// </summary>
    [DockerAvailableFact]
    public async Task PublishAsync_RefusesKnownIncidentRestingOnlyOnAMemorySearchToolResult()
    {
        using var scope = await CreateScopeAsync(postgres);
        var ingested = await PostIngestAsync(scope.Client, "memory-confirmation-tool-result");
        var claimed = await ClaimAsync(scope, ingested.JobId!.Value, "worker-memory-confirmation-tool-result");
        var artifactId = await InsertMemorySearchToolResultAsync(
            scope.ConnectionString, claimed.Id, claimed.Attempt);

        var exception = await Assert.ThrowsAsync<TriageReportValidationException>(() => PublishAsync(
            scope,
            claimed,
            "worker-memory-confirmation-tool-result",
            "KnownIncident",
            [artifactId],
            DocumentationFitStatus.Missing));

        Assert.Equal(MemoryCitationConfirmationRule.UnconfirmedMemoryCitationRefusal, exception.Message);
        Assert.Equal(0, await ReportCountAsync(scope.ConnectionString, ingested.FaultId));
    }

    /// <summary>
    /// And it is a bar on what may carry the classification alone, not a ban on citing the tool
    /// result: beside a confirmed memory document the same report publishes.
    /// </summary>
    [DockerAvailableFact]
    public async Task PublishAsync_AcceptsAMemorySearchToolResultBesideAConfirmedMemoryDocument()
    {
        using var scope = await CreateScopeAsync(postgres);
        var ingested = await PostIngestAsync(scope.Client, "memory-confirmation-tool-result-mixed");
        var claimed = await ClaimAsync(scope, ingested.JobId!.Value, "worker-memory-confirmation-tool-result-mixed");
        var toolResultId = await InsertMemorySearchToolResultAsync(
            scope.ConnectionString, claimed.Id, claimed.Attempt);
        var confirmed = await InsertRetrievedMemoryArtifactAsync(
            scope.ConnectionString, claimed.Id, claimed.Attempt, "Current", MemoryRetrievalConfidence.High);

        await PublishAsync(
            scope,
            claimed,
            "worker-memory-confirmation-tool-result-mixed",
            "KnownIncident",
            [toolResultId, confirmed],
            DocumentationFitStatus.Current);

        Assert.Equal(1, await ReportCountAsync(scope.ConnectionString, ingested.FaultId));
    }

    [DockerAvailableFact]
    public async Task PublishAsync_AcceptsAnotherCompletedClassificationCitingOnlyAnUnconfirmedMemoryDocument()
    {
        using var scope = await CreateScopeAsync(postgres);
        var ingested = await PostIngestAsync(scope.Client, "memory-confirmation-other-class");
        var claimed = await ClaimAsync(scope, ingested.JobId!.Value, "worker-memory-confirmation-other-class");
        var artifactId = await InsertRetrievedMemoryArtifactAsync(
            scope.ConnectionString, claimed.Id, claimed.Attempt, "Current", MemoryRetrievalConfidence.Low);

        await PublishAsync(
            scope,
            claimed,
            "worker-memory-confirmation-other-class",
            "SimpleKnownError",
            [artifactId],
            DocumentationFitStatus.Current);

        Assert.Equal(1, await ReportCountAsync(scope.ConnectionString, ingested.FaultId));
    }

    [DockerAvailableFact]
    public async Task PublishAsync_LeavesAnInsufficientEvidenceReportUntouched()
    {
        using var scope = await CreateScopeAsync(postgres);
        var ingested = await PostIngestAsync(scope.Client, "memory-confirmation-insufficient");
        var claimed = await ClaimAsync(scope, ingested.JobId!.Value, "worker-memory-confirmation-insufficient");
        var artifactId = await InsertRetrievedMemoryArtifactAsync(
            scope.ConnectionString, claimed.Id, claimed.Attempt, "Current", MemoryRetrievalConfidence.Low);

        using var serviceScope = scope.Factory.Services.CreateScope();
        await serviceScope.ServiceProvider.GetRequiredService<ITriageReportRepository>().PublishAsync(
            claimed,
            "worker-memory-confirmation-insufficient",
            Report("Unknown", [artifactId], DocumentationFitStatus.Current) with
            {
                Status = TriageReportStatus.InsufficientEvidence
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, await ReportCountAsync(scope.ConnectionString, ingested.FaultId));
    }

    /// <summary>
    /// The backend's own report must not become unpublishable. It states <c>Unknown</c> with
    /// <c>InsufficientEvidence</c> and cites only job-level intake artifacts, so nothing about a
    /// classification-scoped rule can reach it; this publishes the real one, on a job that does hold
    /// an unconfirmed memory artifact, rather than asserting that from the source.
    /// </summary>
    [DockerAvailableFact]
    public async Task PublishAsync_StillPublishesTheBackendAuthoredNoProgressReport()
    {
        using var scope = await CreateScopeAsync(postgres);
        var ingested = await PostIngestAsync(scope.Client, "memory-confirmation-no-progress");
        var claimed = await ClaimAsync(scope, ingested.JobId!.Value, "worker-memory-confirmation-no-progress");
        await InsertRetrievedMemoryArtifactAsync(
            scope.ConnectionString, claimed.Id, claimed.Attempt, "Current", MemoryRetrievalConfidence.Low);
        var triggerId = await ReadArtifactIdAsync(scope.ConnectionString, claimed.Id, "TriggerSignal");
        var report = NoProgressTerminationReport.Create(
            [
                new TriageArtifact(
                    triggerId, claimed.Id, null, ArtifactKind.TriggerSignal, null, default,
                    "no-progress-trigger", DateTimeOffset.UtcNow)
            ],
            NoProgressTerminationReason.NoRecoveryLeft);

        using var serviceScope = scope.Factory.Services.CreateScope();
        await serviceScope.ServiceProvider.GetRequiredService<ITriageReportRepository>().PublishAsync(
            claimed, "worker-memory-confirmation-no-progress", report, TestContext.Current.CancellationToken);

        var status = await ScalarAsync<string>(
            scope.ConnectionString,
            "SELECT status FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;",
            ("fault_id", ingested.FaultId));
        Assert.Equal("InsufficientEvidence", status);
    }

    /// <summary>
    /// The end-to-end half: the refusal has to survive the reprompt allowlist and be the text the
    /// orchestrator reads on its correction turn, not the content-free fallback. The scripted model
    /// publishes a <c>KnownIncident</c> on an unconfirmed document first, then a classification its
    /// evidence supports, which is the remedy the shipped instruction names.
    /// </summary>
    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_RefusalReachesTheModelVerbatimAsANamedReprompt()
    {
        var citation = new CitationHolder();
        var orchestratorPrompts = new ConcurrentQueue<string>();
        using var scope = await CreateScopeAsync(postgres, services =>
        {
            services.RemoveAll<IAiModelClient>();
            services.AddScoped<IAiModelClient>(_ => new UnconfirmedThenCorrectedModelClient(citation, orchestratorPrompts));
        });
        var ingested = await PostIngestAsync(scope.Client, "memory-confirmation-reprompt");
        var claimed = await ClaimAsync(scope, ingested.JobId!.Value, "worker-memory-confirmation-reprompt");
        citation.ArtifactId = await InsertRetrievedMemoryArtifactAsync(
            scope.ConnectionString, claimed.Id, claimed.Attempt, "Current", MemoryRetrievalConfidence.Low);

        using (var serviceScope = scope.Factory.Services.CreateScope())
        {
            await serviceScope.ServiceProvider.GetRequiredService<ITriageJobRunner>().ProcessClaimedAsync(
                claimed,
                "worker-memory-confirmation-reprompt",
                new TriageJobProcessingSettings(1, TimeSpan.FromSeconds(1)),
                TestContext.Current.CancellationToken);
        }

        var jobStatus = await ScalarAsync<string>(
            scope.ConnectionString,
            "SELECT status FROM incidentcompass.triage_jobs WHERE id = @job_id;",
            ("job_id", claimed.Id));
        var classification = await ScalarAsync<string>(
            scope.ConnectionString,
            "SELECT classification FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;",
            ("fault_id", ingested.FaultId));
        var repromptRationale = await ReadRepromptRationaleAsync(scope.ConnectionString, claimed.Id);

        Assert.Equal("Succeeded", jobStatus);
        Assert.Equal("SimpleKnownError", classification);
        Assert.Contains(
            orchestratorPrompts,
            prompt => prompt.Contains(MemoryCitationConfirmationRule.UnconfirmedMemoryCitationRefusal, StringComparison.Ordinal));
        Assert.StartsWith("orchestrator_reprompt:", repromptRationale, StringComparison.Ordinal);
        Assert.Contains("publish_report_validation_failed", repromptRationale, StringComparison.Ordinal);
        Assert.Contains(
            MemoryCitationConfirmationRule.UnconfirmedMemoryCitationRefusal,
            repromptRationale,
            StringComparison.Ordinal);
    }

    // The grounder is constructed from the configured GitHub repository, whose options record is
    // init-only and therefore not settable from a Configure delegate. Replacing the grounder
    // registration is the narrower substitution: it names the configured repository and leaves every
    // other publication collaborator as the host composed it.
    private static void ConfigureTicketRepository(IServiceCollection services)
    {
        services.RemoveAll<PostgresReportEvidenceGrounder>();
        services.AddScoped(_ => new PostgresReportEvidenceGrounder(TicketRepository));
    }

    private static Task PublishAsync(
        TriageReportTestScope scope,
        TriageJob claimed,
        string workerId,
        string classification,
        Guid artifactId) =>
        PublishAsync(scope, claimed, workerId, classification, [artifactId], DocumentationFitStatus.Missing);

    private static async Task PublishAsync(
        TriageReportTestScope scope,
        TriageJob claimed,
        string workerId,
        string classification,
        IReadOnlyList<Guid> artifactIds,
        DocumentationFitStatus documentationFit)
    {
        using var serviceScope = scope.Factory.Services.CreateScope();
        await serviceScope.ServiceProvider.GetRequiredService<ITriageReportRepository>().PublishAsync(
            claimed,
            workerId,
            Report(classification, artifactIds, documentationFit),
            TestContext.Current.CancellationToken);
    }

    private static TriageReport Report(
        string classification,
        IReadOnlyList<Guid> artifactIds,
        DocumentationFitStatus documentationFit) =>
        new(
            TriageReportStatus.Completed,
            "Grounded report.",
            classification,
            "Medium",
            artifactIds.Select(static id => new TriageReportEvidenceReference(id.ToString(), null)).ToArray(),
            [],
            "Review the cited evidence.")
        {
            DocumentationFit = documentationFit
        };

    private static Task<long> ReportCountAsync(string connectionString, Guid faultId) =>
        ScalarAsync<long>(
            connectionString,
            "SELECT COUNT(*) FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;",
            ("fault_id", faultId));

    private static async Task<string> ReadRepromptRationaleAsync(string connectionString, Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT rationale
            FROM incidentcompass.triage_ledger
            WHERE job_id = @job_id
              AND event_type = 'BudgetEvent'
              AND rationale LIKE 'orchestrator_reprompt:%'
            ORDER BY id
            LIMIT 1;
            """, connection);
        command.Parameters.AddWithValue("job_id", jobId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private sealed class CitationHolder
    {
        public Guid ArtifactId { get; set; }
    }

    /// <summary>
    /// Publishes a <c>KnownIncident</c> on the unconfirmed document, then, once the correction turn
    /// carries the refusal, publishes the same citation under a classification the rule does not
    /// reach. That second call is the remedy the shipped orchestrator instruction names.
    /// </summary>
    private sealed class UnconfirmedThenCorrectedModelClient(
        CitationHolder citation,
        ConcurrentQueue<string> orchestratorPrompts) : IAiModelClient
    {
        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            var toolNames = request.Tools?.Select(static tool => tool.Name).ToHashSet(StringComparer.Ordinal) ?? [];
            if (!toolNames.SetEquals(["delegate", "publish_report"]))
            {
                return Task.FromResult(Response(request, "{\"keyFacts\":[],\"candidateClassification\":\"Unknown\",\"needsDeeperContext\":false,\"rationale\":\"Worker output.\"}", []));
            }

            var transcript = string.Join("\n", request.Messages.Select(static message => message.Content));
            orchestratorPrompts.Enqueue(transcript);
            var classification = transcript.Contains("publish_report_validation_failed", StringComparison.Ordinal)
                ? "SimpleKnownError"
                : "KnownIncident";
            return Task.FromResult(Response(request, "publish", [PublishCall(citation.ArtifactId, classification)]));
        }

        private static AiModelResponse Response(
            AiModelRequest request,
            string content,
            IReadOnlyList<AiToolCall> toolCalls) =>
            new(content, request.Model, "memory-confirmation-test", new AiModelUsage(10, 5, 15), request.CorrelationId, toolCalls);

        private static AiToolCall PublishCall(Guid artifactId, string classification)
        {
            var arguments = JsonSerializer.SerializeToElement(new
            {
                report_json = new
                {
                    status = "Completed",
                    summary = "Grounded report.",
                    classification,
                    confidence = "Medium",
                    documentationFit = "Current",
                    evidence = new[] { new { referenceId = artifactId.ToString() } },
                    limitations = Array.Empty<string>(),
                    recommendedNextAction = "Review the cited evidence."
                }
            });
            return new AiToolCall("publish-" + Guid.NewGuid().ToString("N"), "publish_report", "v1", arguments);
        }
    }
}
