using System.Text;
using IncidentCompass.Application.Core.Exceptions;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Infrastructure.Remediation;
using IncidentCompass.Infrastructure.SourceContext;
using Microsoft.Extensions.DependencyInjection;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// What the remediation-diff table actually holds, and what it refuses to hold.
/// </summary>
/// <remarks>
/// The record's two most important claims are enforced by the schema rather than by the writer: that
/// a stored diff fits the raw budget an approval can carry, and that no row can say a test ran. Both
/// are asserted here against a real PostgreSQL instance, because a check constraint that is only
/// read in a review is a check constraint nobody has run.
/// </remarks>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class RemediationDiffPersistenceTests(PostgresRepositoryFixture postgres)
{
    private const string Patch =
        "--- a/src/Checkout.cs\n+++ b/src/Checkout.cs\n@@ -1,1 +1,1 @@\n-old\n+new\n";

    [DockerAvailableFact]
    public async Task AddAsync_PersistsTheDiffTheBaseTheResultAndThatNoTestRan()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        using var services = ActionApprovalTestSupport.CreateServices(database.ConnectionString);
        var diff = CreateDiff(origin, Patch);

        await services.GetRequiredService<IRemediationDiffRepository>().AddAsync(
            diff,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, await ActionApprovalTestSupport.CountAsync(
            database.ConnectionString,
            """
            SELECT count(*) FROM incidentcompass.remediation_diffs
            WHERE id = @id
              AND tenant_id = @tenant_id
              AND report_id = @report_id
              AND job_id = @job_id
              AND base_tree_identity = @base
              AND result_tree_identity = @result
              AND patch_text = @patch
              AND patch_bytes = octet_length(@patch)
              AND validation_code = 'remediation_applied'
              AND test_outcome = 'not_executed'
              AND test_command_id IS NULL;
            """,
            ("id", diff.Id),
            ("tenant_id", origin.TenantId),
            ("report_id", origin.ReportId),
            ("job_id", origin.JobId),
            ("base", diff.BaseTreeIdentity),
            ("result", diff.ResultTreeIdentity),
            ("patch", Patch)));
    }

    /// <summary>
    /// The stored bound and the parse bound are the same number, and the database is the backstop
    /// that proves it: a diff one byte over the raw budget cannot be stored, whatever a writer
    /// believes.
    /// </summary>
    [DockerAvailableFact]
    public async Task AddAsync_RefusesADiffLargerThanTheRawBudgetAnApprovalCanCarry()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        using var services = ActionApprovalTestSupport.CreateServices(database.ConnectionString);
        var oversized = Patch + new string('x', SourcePatchLimits.RawBudgetBytes);

        await Assert.ThrowsAsync<PersistenceException>(() =>
            services.GetRequiredService<IRemediationDiffRepository>().AddAsync(
                CreateDiff(origin, oversized),
                TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Claiming a test ran is refused by the schema, not merely left unset by the writer.
    /// </summary>
    [DockerAvailableFact]
    public async Task AddAsync_RefusesARowThatClaimsATestRan()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        using var services = ActionApprovalTestSupport.CreateServices(database.ConnectionString);
        var claimed = CreateDiff(origin, Patch) with
        {
            TestOutcome = "passed",
            TestCommandId = "dotnet-test"
        };

        await Assert.ThrowsAsync<PersistenceException>(() =>
            services.GetRequiredService<IRemediationDiffRepository>().AddAsync(
                claimed,
                TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A composed host resolves the pass, its adapter and its writer.
    /// </summary>
    /// <remarks>
    /// Nothing schedules a remediation pass yet, so no other test would notice a registration that
    /// cannot be satisfied until the commit that adds scheduling discovered it at run time. The
    /// runner is deliberately registered in the Application half while the model caller it composes
    /// is registered in the Infrastructure half, which is the arrangement this asserts still works.
    /// </remarks>
    [Fact]
    public void Composition_ResolvesTheRemediationPassWithItsLocalAdapterAndItsWriter()
    {
        using var services = ActionApprovalTestSupport.CreateServices(
            "Host=localhost;Port=1;Database=unused;Username=unused;Password=unused");
        using var scope = services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<RemediationDiffRunner>());
        Assert.IsType<LocalSourceRemediationWorkspace>(
            scope.ServiceProvider.GetRequiredService<IRemediationWorkspace>());
        Assert.IsType<PostgresRemediationDiffRepository>(
            scope.ServiceProvider.GetRequiredService<IRemediationDiffRepository>());
    }

    private static RemediationDiff CreateDiff(ActionApprovalOriginFixture origin, string patch) => new(
        Guid.NewGuid(),
        origin.TenantId,
        origin.ReportId,
        origin.JobId,
        Attempt: 1,
        "orders",
        "v1",
        new string('a', 64),
        new string('b', 64),
        FilesChanged: 1,
        Encoding.UTF8.GetByteCount(patch),
        patch,
        "report-chat",
        "test-model",
        RemediationCodes.Applied,
        DateTimeOffset.UtcNow);
}
