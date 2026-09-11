namespace IncidentCompass.Infrastructure.Postgres;

internal static class PostgresMigrationCatalog
{
    public static readonly IReadOnlyList<PostgresSchemaMigration> All =
    [
        Released(1, "v0.1.1-baseline",
        [
            "001-enable-pgvector.sql",
            "004-observability-cost.sql",
            "006-tool-audit.sql",
            "007-intake.sql",
            "008-triage-ledger.sql",
            "009-triage-reports-minimal.sql",
            "010-memory.sql"
        ]),
        Released(2, "v0.2-memory-file-sync", ["011-memory-file-sync.sql"]),
        Released(3, "v0.2-signal-delivery-idempotency", ["012-signal-delivery-idempotency.sql"]),
        Released(4, "v0.2-memory-seed-generations", ["013-memory-seed-generations.sql"]),
        Released(5, "v0.2-documentation-fit", ["014-documentation-fit.sql"]),
        Released(6, "v0.2-versioned-fault-grouping", ["015-versioned-fault-grouping.sql"]),
        Released(7, "v0.2-scoped-suppression", ["016-scoped-suppression.sql"]),
        Released(8, "v0.2-recurrence-state", ["017-recurrence-state.sql"]),
        Released(9, "v0.2-report-lifecycle", ["018-report-lifecycle.sql"]),
        Released(10, "v0.2-retriage-jobs", ["019-retriage-jobs.sql"]),
        Released(11, "v0.2-report-listing", ["020-report-listing.sql"]),
        Released(12, "v0.2-memory-seed-sync-status", ["021-memory-seed-sync-status.sql"]),
        Released(13, "v0.2-triage-job-retry-budget", ["022-triage-job-retry-budget.sql"]),
        Released(14, "v0.3-action-approvals-outbox", ["023-action-approvals-outbox.sql"]),
        Released(15, "v0.3-post-report-action-intents", ["024-post-report-action-intents.sql"]),
        Released(16, "v0.3-external-action-audit-projection", ["025-external-action-audit-projection.sql"]),
        Released(17, "v0.3-model-cost-rollup-index", ["026-model-cost-rollup-index.sql"]),
        new(18, "v0.4-artifact-redaction-marker", ["027-artifact-redaction-marker.sql"]),
        new(19, "v0.4-signal-payload-and-artifact-retention",
            ["028-signal-payload-and-artifact-retention.sql"]),
        new(20, "v0.4-report-model-provenance", ["029-report-model-provenance.sql"]),
        new(21, "v0.4-model-price-administration", ["030-model-price-administration.sql"]),
        new(22, "v0.4-memory-corpus-generations", ["031-memory-corpus-generations.sql"]),
        new(23, "v0.4-action-approval-fault-correlation",
            ["032-action-approval-fault-correlation.sql"]),
        new(24, "v0.4-remediation-diffs", ["033-remediation-diffs.sql"]),
        new(25, "v0.4-branch-push-audit-projection", ["034-branch-push-audit-projection.sql"])
    ];

    private static PostgresSchemaMigration Released(
        int version,
        string name,
        IReadOnlyList<string> scriptNames) =>
        new(version, name, scriptNames, AcceptsReleasedLegacyChecksums: true);
}
