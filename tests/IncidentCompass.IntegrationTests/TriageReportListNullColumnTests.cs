using IncidentCompass.Application.Investigation.Reports.List;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// the report-list reader resolves its columns by name and guards exactly the columns
/// the schema declares nullable. A report whose optional columns are all NULL and that has no
/// successor exercises every guard at once, so a reordered SELECT list or a missing guard fails
/// here instead of silently shifting mapped fields.
/// </summary>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class TriageReportListNullColumnTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task ListAsync_ReportWithNullOptionalColumns_MapsEveryFieldByName()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var serviceName = "report-list-null-svc";
        var faultId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await SeedReportWithNullOptionalColumnsAsync(
            database.ConnectionString, faultId, reportId, serviceName);

        using var services = ActionApprovalTestSupport.CreateServices(database.ConnectionString);
        using var scope = services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ITriageReportListRepository>();
        var reports = await repository.ListAsync(
            new TriageReportListFilter(null, serviceName, null, null, null, null, null, 10),
            "local",
            TestContext.Current.CancellationToken);

        var item = Assert.Single(reports);
        Assert.Equal(reportId, item.Id);
        Assert.Equal(faultId, item.FaultId);
        Assert.Equal("Completed", item.Status);
        Assert.Equal("Null column probe summary.", item.Summary);
        Assert.Equal("SimpleKnownError", item.Classification);
        Assert.Equal("Medium", item.Confidence);
        Assert.Null(item.IsMassIssue);
        Assert.Equal(string.Empty, item.RecommendedNextAction);
        Assert.Equal(serviceName, item.ServiceName);
        Assert.Equal("prod", item.Environment);
        Assert.Null(item.SupersedesReportId);
        Assert.Null(item.SupersededByReportId);
        Assert.True(item.IsLatestForFault);
    }

    private static async Task SeedReportWithNullOptionalColumnsAsync(
        string connectionString,
        Guid faultId,
        Guid reportId,
        string serviceName)
    {
        var configHash = "report-list-null-" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(connectionString, """
            INSERT INTO incidentcompass.triage_config_snapshots (
                config_hash, serialized_config, instructions, created_at_utc)
            VALUES (@config_hash, '{}'::jsonb, '{}'::jsonb, now());

            INSERT INTO incidentcompass.signals (
                id, tenant_id, source, service_name, environment, summary, body,
                observed_at_utc, received_at_utc)
            VALUES (
                @signal_id, 'local', 'tester', @service_name, 'prod',
                'Null column probe signal.', '{}'::jsonb, now(), now());

            INSERT INTO incidentcompass.faults (
                id, trigger_signal_id, tenant_id, status, fingerprint, fingerprint_version,
                fingerprint_strength, service_name, environment, created_at_utc)
            VALUES (
                @fault_id, @signal_id, 'local', 'Completed', @fingerprint, 1,
                'strong', @service_name, 'prod', now());

            INSERT INTO incidentcompass.triage_reports (
                id, fault_id, status, summary, classification, confidence,
                is_mass_issue, recommended_next_action, supersedes_report_id,
                limitations, config_hash, created_at_utc)
            VALUES (
                @report_id, @fault_id, 'Completed', 'Null column probe summary.',
                'SimpleKnownError', 'Medium', NULL, NULL, NULL,
                ARRAY[]::text[], @config_hash, now());
            """,
            ("config_hash", configHash),
            ("fault_id", faultId),
            ("signal_id", Guid.NewGuid()),
            ("fingerprint", "report-list-null-" + Guid.NewGuid().ToString("N")),
            ("service_name", serviceName),
            ("report_id", reportId));
    }

    private static async Task ExecuteAsync(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
