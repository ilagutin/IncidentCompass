using IncidentCompass.Application.Investigation.Reports.Redaction;
using IncidentCompass.Domain.Incidents;
using Microsoft.Extensions.DependencyInjection;
using static IncidentCompass.IntegrationTests.TriageReportGroundingTestSupport;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// The read behind the published redaction marker. Its answer decides whether a report tells its
/// reader that part of the evidence was withheld, and the input is a list the model authored and
/// ordered, so the properties worth asserting are the ones an adversarial citation list would
/// attack: that every citation is looked at, that the reference form the prompt hands the model
/// resolves, and that the attempt fence holds.
/// </summary>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class PostgresCitedEvidenceRedactionRepositoryTests(PostgresRepositoryFixture postgres)
{
    /// <summary>
    /// The prompt names artifacts as <c>artifact:{id}</c>, so that is the form the model echoes back
    /// most of the time, and a bare id is the other. Both have to resolve, unparseable references are
    /// skipped rather than thrown on, and the attempt fence excludes a row belonging to a different
    /// attempt of the same job while keeping the job-level rows intake wrote with a NULL attempt.
    /// </summary>
    [DockerAvailableFact]
    public async Task ReadCitedAsync_ResolvesBothReferenceFormsAndHoldsTheAttemptFence()
    {
        using var scope = await CreateScopeAsync(postgres);
        var claimed = await ClaimFreshJobAsync(scope, "cited-redaction-forms");
        var jobLevelArtifactId = await ReadArtifactIdAsync(scope.ConnectionString, claimed.Id, "TriggerSignal");
        var thisAttemptArtifactId = await InsertArtifactAsync(
            scope.ConnectionString, claimed.Id, claimed.Attempt, redactionApplied: true);
        var otherAttemptArtifactId = await InsertArtifactAsync(
            scope.ConnectionString, claimed.Id, claimed.Attempt + 1, redactionApplied: true);

        var cited = await ReadCitedAsync(
            scope,
            claimed,
            [
                "artifact:" + thisAttemptArtifactId,
                jobLevelArtifactId.ToString(),
                "artifact:" + otherAttemptArtifactId,
                "artifact:not-a-guid",
                string.Empty
            ]);

        // Two Singles plus the count assert both membership and that nothing else came back, which is
        // what excludes the other attempt's row rather than a separate negative on its own.
        Assert.Equal(2, cited.Count);
        Assert.True(cited.Single(entry => entry.ArtifactId == thisAttemptArtifactId).RedactionApplied);
        Assert.Null(cited.Single(entry => entry.ArtifactId == jobLevelArtifactId).RedactionApplied);
        Assert.DoesNotContain(cited, entry => entry.ArtifactId == otherAttemptArtifactId);
    }

    /// <summary>
    /// The read must not truncate. The model authors and orders its own evidence array, so a read
    /// that answered for only the first N citations would let it hide a real withholding by listing
    /// enough clean artifacts first - and truncation can only fail in that direction, because
    /// dropping a citation can turn a "true" into nothing but never the reverse. This cites well past
    /// any plausible cap, with the redacted artifact last.
    /// </summary>
    [DockerAvailableFact]
    public async Task ReadCitedAsync_StillFindsTheRedactedArtifact_WhenManyCleanCitationsPrecedeIt()
    {
        const int cleanCitations = 150;
        using var scope = await CreateScopeAsync(postgres);
        var claimed = await ClaimFreshJobAsync(scope, "cited-redaction-bulk");
        var cleanArtifactIds = await InsertCleanArtifactsAsync(
            scope.ConnectionString, claimed.Id, claimed.Attempt, cleanCitations);
        var redactedArtifactId = await InsertArtifactAsync(
            scope.ConnectionString, claimed.Id, claimed.Attempt, redactionApplied: true);

        var cited = await ReadCitedAsync(
            scope,
            claimed,
            [.. cleanArtifactIds.Select(id => "artifact:" + id), "artifact:" + redactedArtifactId]);

        Assert.Equal(cleanCitations + 1, cited.Count);
        Assert.True(cited.Single(entry => entry.ArtifactId == redactedArtifactId).RedactionApplied);
        Assert.Contains(cited, entry => entry.RedactionApplied == true);
    }

    private static async Task<IReadOnlyList<CitedEvidenceRedaction>> ReadCitedAsync(
        TriageReportTestScope scope,
        TriageJob claimed,
        IReadOnlyCollection<string> referenceIds)
    {
        using var serviceScope = scope.Factory.Services.CreateScope();
        return await serviceScope.ServiceProvider
            .GetRequiredService<ICitedEvidenceRedactionRepository>()
            .ReadCitedAsync(claimed.Id, claimed.Attempt, referenceIds, TestContext.Current.CancellationToken);
    }

    private static async Task<TriageJob> ClaimFreshJobAsync(TriageReportTestScope scope, string signalKey)
    {
        var serviceName = signalKey + "-" + Guid.NewGuid().ToString("N");
        var ingested = await PostIngestAsync(scope.Client, new TriageReportTesterEnvelope(
            "tester", serviceName, "prod", DateTimeOffset.UtcNow,
            new TriageReportTesterAttributes("ExampleException", signalKey, "/source")));
        return await ClaimAsync(scope, ingested.JobId!.Value, "worker-" + signalKey);
    }

    private static async Task<Guid> InsertArtifactAsync(
        string connectionString,
        Guid jobId,
        int attempt,
        bool? redactionApplied)
    {
        var artifactId = Guid.NewGuid();
        await ExecuteAsync(connectionString, """
            INSERT INTO incidentcompass.triage_artifacts (
                id, job_id, attempt, kind, domain_ref, redacted_payload, content_hash, created_at_utc,
                redaction_applied)
            VALUES (
                @id, @job_id, @attempt, 'RetrievedItem', 'source:r1:src/Checkout.cs',
                '{"excerpt":"excerpt"}'::jsonb, @id::text, now(), @redaction_applied);
            """,
            ("id", artifactId),
            ("job_id", jobId),
            ("attempt", attempt),
            ("redaction_applied", (object?)redactionApplied ?? DBNull.Value));
        return artifactId;
    }

    private static async Task<IReadOnlyList<Guid>> InsertCleanArtifactsAsync(
        string connectionString,
        Guid jobId,
        int attempt,
        int count)
    {
        var artifactIds = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        await ExecuteAsync(connectionString, """
            INSERT INTO incidentcompass.triage_artifacts (
                id, job_id, attempt, kind, domain_ref, redacted_payload, content_hash, created_at_utc,
                redaction_applied)
            SELECT source.id, @job_id, @attempt, 'RetrievedItem', 'source:r1:src/Filler.cs',
                   '{"excerpt":"filler"}'::jsonb, source.id::text, now(), false
            FROM unnest(@ids::uuid[]) AS source(id);
            """,
            ("job_id", jobId),
            ("attempt", attempt),
            ("ids", artifactIds));
        return artifactIds;
    }
}
