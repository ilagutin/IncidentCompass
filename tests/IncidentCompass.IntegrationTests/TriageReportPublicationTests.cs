using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Domain.Incidents;
using Microsoft.Extensions.DependencyInjection;
using static IncidentCompass.IntegrationTests.TriageReportGroundingTestSupport;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class TriageReportPublicationTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task PublishAsync_StaleAttemptIsRejectedByFence()
    {
        using var scope = await CreateScopeAsync(postgres);
        var ingested = await PostIngestAsync(scope.Client, "stale-fence");
        var claimed = await ClaimAsync(scope, ingested.JobId!.Value, "worker-stale-1");
        var triggerArtifactId = await ReadArtifactIdAsync(scope.ConnectionString, claimed.Id, "TriggerSignal");
        await ExecuteAsync(scope.ConnectionString, """
            UPDATE incidentcompass.triage_jobs
            SET attempt = 2, locked_by = 'worker-stale-2', locked_until_utc = now() + interval '5 minutes'
            WHERE id = @job_id;
            """, ("job_id", claimed.Id));

        using var serviceScope = scope.Factory.Services.CreateScope();
        var repository = serviceScope.ServiceProvider.GetRequiredService<ITriageReportRepository>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.PublishAsync(
            claimed,
            "worker-stale-1",
            CreateReport(triggerArtifactId),
            TestContext.Current.CancellationToken));

        var reports = await ScalarAsync<long>(scope.ConnectionString, "SELECT COUNT(*) FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;", ("fault_id", ingested.FaultId));
        Assert.Equal(0, reports);
    }


    [DockerAvailableFact]
    public async Task PublishAsync_DerivesAndPersistsDocumentationFitFromCitedMemoryItems()
    {
        var cases = new (string[] DocumentationStatuses, DocumentationFitStatus ExpectedFit, string? ExpectedLimitation)[]
        {
            (["Current"], DocumentationFitStatus.Current, null),
            (["Current", "Stale"], DocumentationFitStatus.CurrentWithHistorical, null),
            (["Stale"], DocumentationFitStatus.StaleOnly, null),
            ([], DocumentationFitStatus.Missing, null),
            (["Unversioned"], DocumentationFitStatus.Missing, "Cited documentation is unversioned or service-mismatched, so its currentness cannot be assessed."),
            (["Current", "Current"], DocumentationFitStatus.MultipleCurrentDocuments, "Multiple current documents were cited; their compatibility requires operator review.")
        };
        foreach (var testCase in cases)
        {
            using var scope = await CreateScopeAsync(postgres);
            var ingested = await PostIngestAsync(scope.Client, "documentation-fit");
            var claimed = await ClaimAsync(scope, ingested.JobId!.Value, "worker-documentation-fit");
            var artifactIds = new List<Guid>();
            foreach (var documentationStatus in testCase.DocumentationStatuses)
            {
                artifactIds.Add(await InsertRetrievedMemoryArtifactAsync(
                    scope.ConnectionString,
                    claimed.Id,
                    claimed.Attempt,
                    documentationStatus));
            }

            if (artifactIds.Count == 0)
            {
                artifactIds.Add(await ReadArtifactIdAsync(scope.ConnectionString, claimed.Id, "TriggerSignal"));
            }

            using var serviceScope = scope.Factory.Services.CreateScope();
            var repository = serviceScope.ServiceProvider.GetRequiredService<ITriageReportRepository>();
            var reportId = await repository.PublishAsync(
                claimed,
                "worker-documentation-fit",
                CreateReport(artifactIds[0]) with
                {
                    DocumentationFit = testCase.ExpectedFit,
                    Evidence = artifactIds.Select(static id => new TriageReportEvidenceReference(id.ToString(), null)).ToArray()
                },
                TestContext.Current.CancellationToken);

            var persistedFit = await ScalarAsync<string>(
                scope.ConnectionString,
                "SELECT documentation_fit FROM incidentcompass.triage_reports WHERE id = @report_id;",
                ("report_id", reportId));
            Assert.Equal(testCase.ExpectedFit.ToString(), persistedFit);

            var response = await scope.Client.GetAsync(
                "/api/v1/triage-reports/" + reportId,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var details = await response.Content.ReadFromJsonAsync<DocumentationFitDetailsDto>(TestContext.Current.CancellationToken);
            Assert.NotNull(details);
            Assert.Equal(testCase.ExpectedFit.ToString(), details.DocumentationFit);
            if (testCase.ExpectedLimitation is null)
            {
                Assert.Empty(details.Limitations);
            }
            else
            {
                Assert.Contains(testCase.ExpectedLimitation, details.Limitations);
            }
        }
    }

    [DockerAvailableFact]
    public async Task PublishAsync_RejectsModelDocumentationFitThatDisagreesWithCitedMemoryItems()
    {
        using var scope = await CreateScopeAsync(postgres);
        var ingested = await PostIngestAsync(scope.Client, "documentation-fit-rejection");
        var claimed = await ClaimAsync(scope, ingested.JobId!.Value, "worker-documentation-fit-rejection");
        var artifactId = await InsertRetrievedMemoryArtifactAsync(
            scope.ConnectionString,
            claimed.Id,
            claimed.Attempt,
            "Current");

        using var serviceScope = scope.Factory.Services.CreateScope();
        var repository = serviceScope.ServiceProvider.GetRequiredService<ITriageReportRepository>();
        await Assert.ThrowsAsync<TriageReportValidationException>(() => repository.PublishAsync(
            claimed,
            "worker-documentation-fit-rejection",
            CreateReport(artifactId),
            TestContext.Current.CancellationToken));

        var reports = await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT COUNT(*) FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;",
            ("fault_id", ingested.FaultId));
        Assert.Equal(0, reports);
    }
    [DockerAvailableFact]
    public async Task PublishAsync_RefreshedNeighborSetKeepsStableEvidenceReference()
    {
        using var scope = await CreateScopeAsync(postgres);
        var serviceName = "neighbor-refresh-svc-" + Guid.NewGuid().ToString("N");
        var envelope = new TriageReportTesterEnvelope(
            "tester",
            serviceName,
            "prod",
            DateTimeOffset.UtcNow,
            new TriageReportTesterAttributes("TimeoutException", "Neighbor refresh timeout", "/neighbor-refresh"));
        var ingested = await PostIngestAsync(scope.Client, envelope);
        Assert.NotNull(ingested.JobId);
        var neighborArtifactId = await ReadArtifactIdAsync(scope.ConnectionString, ingested.JobId!.Value, "NeighborSet");

        var attached = await PostIngestAsync(scope.Client, envelope with { ObservedAtUtc = DateTimeOffset.UtcNow.AddSeconds(1) });
        Assert.Equal(ingested.FaultId, attached.FaultId);
        Assert.False(attached.IsNewJob);

        var refreshedNeighborArtifactId = await ReadArtifactIdAsync(scope.ConnectionString, ingested.JobId.Value, "NeighborSet");
        Assert.Equal(neighborArtifactId, refreshedNeighborArtifactId);

        var claimed = await ClaimAsync(scope, ingested.JobId.Value, "worker-neighbor-refresh");
        using var serviceScope = scope.Factory.Services.CreateScope();
        var repository = serviceScope.ServiceProvider.GetRequiredService<ITriageReportRepository>();
        await repository.PublishAsync(
            claimed,
            "worker-neighbor-refresh",
            CreateReport(neighborArtifactId),
            TestContext.Current.CancellationToken);

        var evidence = await ReadSingleEvidenceAsync(scope.ConnectionString, ingested.FaultId);
        Assert.Equal("NeighborSet", evidence.Kind);
    }

    [DockerAvailableFact]
    public async Task PublishAsync_CitesClosedSourceCodePayloadUsingExistingRetrievedItemKind()
    {
        using var scope = await CreateScopeAsync(postgres);
        var serviceName = "source-grounding-" + Guid.NewGuid().ToString("N");
        var ingested = await PostIngestAsync(scope.Client, new TriageReportTesterEnvelope(
            "tester", serviceName, "prod", DateTimeOffset.UtcNow,
            new TriageReportTesterAttributes("ExampleException", "source grounding", "/source")));
        var claimed = await ClaimAsync(scope, ingested.JobId!.Value, "worker-source-grounding");
        await SetCurrentReleaseAsync(scope.ConnectionString, claimed.ConfigHash, serviceName, "r1");
        var artifactId = await InsertSourceArtifactAsync(scope.ConnectionString, claimed.Id, claimed.Attempt, "r1");

        using var serviceScope = scope.Factory.Services.CreateScope();
        var repository = serviceScope.ServiceProvider.GetRequiredService<ITriageReportRepository>();
        await repository.PublishAsync(
            claimed,
            "worker-source-grounding",
            CreateReport(artifactId),
            TestContext.Current.CancellationToken);

        var evidence = await ReadSingleEvidenceAsync(scope.ConnectionString, ingested.FaultId);
        Assert.Equal("RetrievedItem", evidence.Kind);
        var evidenceKind = await ScalarAsync<string>(scope.ConnectionString, """
            SELECT a.redacted_payload->>'evidenceKind'
            FROM incidentcompass.triage_evidence e
            JOIN incidentcompass.triage_artifacts a ON a.id = e.artifact_id
            JOIN incidentcompass.triage_reports r ON r.id = e.report_id
            WHERE r.fault_id = @fault_id;
            """, ("fault_id", ingested.FaultId));
        Assert.Equal("SourceCode", evidenceKind);
    }

    [DockerAvailableFact]
    public async Task PublishAsync_RejectsSourceArtifactForDifferentRelease()
    {
        using var scope = await CreateScopeAsync(postgres);
        var serviceName = "source-stale-" + Guid.NewGuid().ToString("N");
        var ingested = await PostIngestAsync(scope.Client, new TriageReportTesterEnvelope(
            "tester", serviceName, "prod", DateTimeOffset.UtcNow,
            new TriageReportTesterAttributes("ExampleException", "stale source", "/source")));
        var claimed = await ClaimAsync(scope, ingested.JobId!.Value, "worker-source-stale");
        await SetCurrentReleaseAsync(scope.ConnectionString, claimed.ConfigHash, serviceName, "r2");
        var artifactId = await InsertSourceArtifactAsync(scope.ConnectionString, claimed.Id, claimed.Attempt, "r1");

        using var serviceScope = scope.Factory.Services.CreateScope();
        var repository = serviceScope.ServiceProvider.GetRequiredService<ITriageReportRepository>();
        await Assert.ThrowsAsync<TriageReportValidationException>(() => repository.PublishAsync(
            claimed,
            "worker-source-stale",
            CreateReport(artifactId),
            TestContext.Current.CancellationToken));
    }

    [DockerAvailableFact]
    public async Task TriageReportPublisher_AppendsDurableSourceNoMatchLimitation()
    {
        using var scope = await CreateScopeAsync(postgres);
        var ingested = await PostIngestAsync(scope.Client, "source-no-match-policy");
        var claimed = await ClaimAsync(scope, ingested.JobId!.Value, "worker-source-no-match");
        await InsertToolOutcomeAsync(scope.ConnectionString, claimed.Id, claimed.Attempt);
        var triggerId = await ReadArtifactIdAsync(scope.ConnectionString, claimed.Id, "TriggerSignal");
        var arguments = JsonSerializer.SerializeToElement(new
        {
            report_json = new
            {
                status = "Completed",
                summary = "Grounded report.",
                classification = "SimpleKnownError",
                confidence = "Medium",
                documentationFit = "Missing",
                evidence = new[] { new { referenceId = triggerId.ToString() } },
                limitations = Array.Empty<string>(),
                recommendedNextAction = "Review the signal."
            }
        });

        using var serviceScope = scope.Factory.Services.CreateScope();
        var publisher = serviceScope.ServiceProvider.GetRequiredService<TriageReportPublisher>();
        await publisher.PublishAsync(
            claimed,
            "worker-source-no-match",
            new AiToolCall("publish-source-no-match", "publish_report", "v1", arguments),
            TestContext.Current.CancellationToken);

        var limitation = await ScalarAsync<string>(
            scope.ConnectionString,
            "SELECT limitations[1] FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;",
            ("fault_id", ingested.FaultId));
        Assert.Equal("Read-only context source_lookup returned no matches (source_no_match).", limitation);
    }

    /// <summary>
    /// The redaction marker end to end: a cited source artifact whose row records that redaction
    /// removed something, and a published report that says so without the model having been asked.
    /// </summary>
    [DockerAvailableFact]
    public async Task TriageReportPublisher_AppendsDurableRedactedEvidenceLimitation()
    {
        var limitation = await PublishCitingSourceArtifactAsync(
            "redaction-marker",
            redactionApplied: true,
            modelLimitations: []);

        Assert.Equal(
            "Evidence cited by this report includes at least one value that redaction removed" +
            " before the model saw it.",
            limitation);
    }

    /// <summary>
    /// The other half of the same guarantee, and the model-authored half of the spoofing problem.
    /// The cited row records that redaction removed nothing, and the model wrote the reserved
    /// sentence into its own limitations anyway. What is published is what the backend derived.
    /// </summary>
    [DockerAvailableFact]
    public async Task TriageReportPublisher_DropsAModelAuthoredRedactionMarker_WhenNothingWasRedacted()
    {
        var limitation = await PublishCitingSourceArtifactAsync(
            "redaction-marker-spoof",
            redactionApplied: false,
            modelLimitations:
            [
                "Evidence cited by this report includes at least one value that redaction removed" +
                " before the model saw it."
            ]);

        Assert.Equal(string.Empty, limitation);
    }

    /// <summary>
    /// The gap that made the marker model-selectable. <c>ToolResult</c> is a citable artifact kind
    /// holding the same redacted text as the per-item artifacts from the same call, so a row that
    /// recorded nothing let the model decide whether the limitation appeared, simply by citing the
    /// tool result instead. This publishes through the real committer, so what is asserted is the
    /// column that committer wrote.
    /// </summary>
    [DockerAvailableFact]
    public async Task TriageReportPublisher_AppendsTheRedactedEvidenceLimitation_WhenTheCitationIsAToolResult()
    {
        var limitation = await PublishCitingArtifactAsync(
            "redaction-marker-toolresult",
            CommitRedactedToolResultAsync,
            modelLimitations: []);

        Assert.Equal(
            "Evidence cited by this report includes at least one value that redaction removed" +
            " before the model saw it.",
            limitation);
    }

    /// <summary>
    /// Commits a tool result the way <c>WorkerToolCallExecutor</c> does: the connector text goes
    /// through the redaction boundary, and the outcome that boundary computed travels on the commit
    /// request onto the durable <c>ToolResult</c> row.
    /// </summary>
    private static async Task<string> CommitRedactedToolResultAsync(
        TriageReportTestScope scope,
        TriageJob claimed)
    {
        var output = JsonSerializer.SerializeToElement(new
        {
            matched = true,
            items = new[]
            {
                new { quote = "the ticket body pasted AKIA0123456789ABCDEF into the thread" }
            }
        });
        var redacted = RedactedToolArtifactFactory.RedactOutput(output, RedactionSettings.Default);
        Assert.True(redacted.RedactionApplied);

        using var serviceScope = scope.Factory.Services.CreateScope();
        var artifact = await serviceScope.ServiceProvider
            .GetRequiredService<ITriageToolResultCommitter>()
            .CommitSucceededAsync(
                new TriageToolResultCommitRequest(
                    claimed,
                    "investigator",
                    "ticket_search",
                    redacted.Output,
                    CanonicalJsonSerializer.ComputeSha256Hex(
                        CanonicalJsonSerializer.Canonicalize(
                            JsonNode.Parse(redacted.Output.GetRawText())!)),
                    "Tool completed successfully.",
                    redacted.RedactionApplied),
                TestContext.Current.CancellationToken);

        Assert.Equal(ArtifactKind.ToolResult, artifact.Kind);
        return "artifact:" + artifact.Id;
    }

    private Task<string> PublishCitingSourceArtifactAsync(
        string signalKey,
        bool redactionApplied,
        string[] modelLimitations) =>
        PublishCitingArtifactAsync(
            signalKey,
            async (scope, claimed) =>
            {
                var artifactId = await InsertSourceArtifactAsync(
                    scope.ConnectionString, claimed.Id, claimed.Attempt, "r1", redactionApplied);
                return artifactId.ToString();
            },
            modelLimitations);

    private async Task<string> PublishCitingArtifactAsync(
        string signalKey,
        Func<TriageReportTestScope, TriageJob, Task<string>> citeAsync,
        string[] modelLimitations)
    {
        using var scope = await CreateScopeAsync(postgres);
        var serviceName = signalKey + "-" + Guid.NewGuid().ToString("N");
        var ingested = await PostIngestAsync(scope.Client, new TriageReportTesterEnvelope(
            "tester", serviceName, "prod", DateTimeOffset.UtcNow,
            new TriageReportTesterAttributes("ExampleException", signalKey, "/source")));
        var claimed = await ClaimAsync(scope, ingested.JobId!.Value, "worker-" + signalKey);
        await SetCurrentReleaseAsync(scope.ConnectionString, claimed.ConfigHash, serviceName, "r1");
        var referenceId = await citeAsync(scope, claimed);
        var arguments = JsonSerializer.SerializeToElement(new
        {
            report_json = new
            {
                status = "Completed",
                summary = "Grounded report.",
                classification = "SimpleKnownError",
                confidence = "Medium",
                documentationFit = "Missing",
                evidence = new[] { new { referenceId } },
                limitations = modelLimitations,
                recommendedNextAction = "Review the source excerpt."
            }
        });

        using var serviceScope = scope.Factory.Services.CreateScope();
        await serviceScope.ServiceProvider.GetRequiredService<TriageReportPublisher>().PublishAsync(
            claimed,
            "worker-" + signalKey,
            new AiToolCall("publish-" + signalKey, "publish_report", "v1", arguments),
            TestContext.Current.CancellationToken);

        // array_to_string keeps an empty limitations array readable as "" instead of SQL NULL, so the
        // "no marker" case asserts on a value rather than on the absence of a row.
        return await ScalarAsync<string>(
            scope.ConnectionString,
            """
            SELECT array_to_string(limitations, '|')
            FROM incidentcompass.triage_reports
            WHERE fault_id = @fault_id;
            """,
            ("fault_id", ingested.FaultId));
    }

    private sealed record DocumentationFitDetailsDto(string DocumentationFit, IReadOnlyList<string> Limitations);
}
