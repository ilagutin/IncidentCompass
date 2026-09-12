using IncidentCompass.Application.Remediation;
using Microsoft.Extensions.DependencyInjection;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// What a scheduled remediation pass reads back before it asks a model anything.
/// </summary>
/// <remarks>
/// The read is worth a real database because every part of it is a join the compiler cannot check:
/// the report reaches its job and its fault, the tenant is a predicate rather than a filter applied
/// afterwards, and source evidence is separated from the rest of the cited evidence by a pairing of
/// artifact kind and domain reference that only the source-read boundary writes.
/// </remarks>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class RemediationPassContextReadTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task Find_ReadsTheJobTheFaultTheReportAndOnlyTheCitedSourceEvidence()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        var sourceArtifactId = await SeedCitedSourceEvidenceAsync(database.ConnectionString, origin);
        using var services = ActionApprovalTestSupport.CreateServices(database.ConnectionString);

        var context = await services.GetRequiredService<IRemediationPassContextRepository>()
            .FindAsync(origin.TenantId, origin.ReportId, TestContext.Current.CancellationToken);

        Assert.NotNull(context);
        Assert.Equal(origin.JobId, context.Job.Id);
        Assert.Equal(origin.FaultId, context.Fault.Id);
        Assert.Equal(origin.TenantId, context.Fault.TenantId);
        Assert.Equal("orders", context.Fault.ServiceName);
        Assert.Equal("action test report", context.Report.Summary);

        // The seeded report cites two artifacts. Only the one the source boundary wrote is something
        // a diff can be written against; the trigger signal is evidence for the report and not for a
        // change to a file.
        Assert.Equal(2, context.Report.Evidence.Count);
        var evidence = Assert.Single(context.SourceEvidence);
        Assert.Equal(sourceArtifactId, evidence.Id);
        Assert.Equal("src/Checkout.cs", evidence.RedactedPayload.GetProperty("relativePath").GetString());
    }

    /// <summary>
    /// Another tenant's report is not found, rather than found and then filtered. A pass that could
    /// read a report across the tenant boundary would write a diff against a checkout that is not
    /// the caller's.
    /// </summary>
    [DockerAvailableFact]
    public async Task Find_DoesNotReturnAReportBelongingToAnotherTenant()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        using var services = ActionApprovalTestSupport.CreateServices(database.ConnectionString);

        var context = await services.GetRequiredService<IRemediationPassContextRepository>()
            .FindAsync("some-other-tenant", origin.ReportId, TestContext.Current.CancellationToken);

        Assert.Null(context);
    }

    /// <summary>
    /// A report whose evidence names no source file yields a context with empty source evidence, and
    /// the pass refuses on that rather than on a missing read. The two are different failures and the
    /// closed code has to be able to tell them apart.
    /// </summary>
    [DockerAvailableFact]
    public async Task Find_ReturnsAContextWithNoSourceEvidenceWhenTheReportCitesNone()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        using var services = ActionApprovalTestSupport.CreateServices(database.ConnectionString);

        var context = await services.GetRequiredService<IRemediationPassContextRepository>()
            .FindAsync(origin.TenantId, origin.ReportId, TestContext.Current.CancellationToken);

        Assert.NotNull(context);
        Assert.Empty(context.SourceEvidence);
    }

    private static async Task<Guid> SeedCitedSourceEvidenceAsync(
        string connectionString,
        ActionApprovalOriginFixture origin)
    {
        var artifactId = Guid.NewGuid();
        await ActionApprovalTestSupport.ExecuteAsync(connectionString, """
            INSERT INTO incidentcompass.triage_artifacts (
                id, job_id, attempt, kind, domain_ref, redacted_payload, content_hash, created_at_utc)
            VALUES (@artifact_id, @job_id, 1, 'RetrievedItem', @domain_ref,
                    @payload::jsonb, @content_hash, clock_timestamp());

            INSERT INTO incidentcompass.triage_evidence (
                id, report_id, kind, artifact_id, reference, quote, created_at_utc)
            VALUES (gen_random_uuid(), @report_id, 'RetrievedItem', @artifact_id, @domain_ref,
                    'public static decimal Total(decimal p, int q) => p * q - 1;', clock_timestamp());
            """,
            ("artifact_id", artifactId),
            ("job_id", origin.JobId),
            ("report_id", origin.ReportId),
            ("domain_ref", "source:v1:src/Checkout.cs"),
            ("payload", """
                {"evidenceKind":"SourceCode","relativePath":"src/Checkout.cs","lineStart":1,
                 "lineEnd":2,"excerpt":"public static decimal Total(decimal p, int q) => p * q - 1;",
                 "release":"v1","mappingMethod":"exact"}
                """),
            ("content_hash", "source-" + artifactId.ToString("N")));
        return artifactId;
    }
}
