using System.Globalization;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

internal static class PostgresMigrationDurableDataAssertions
{
    public static async Task<Guid> SeedPreProjectionActionAsync(
        string connectionString,
        string proposalKey)
    {
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(connectionString);
        var actionId = Guid.NewGuid();
        await ActionApprovalTestSupport.ExecuteAsync(connectionString, """
            INSERT INTO incidentcompass.action_approvals (
                id, tenant_id, origin_report_id, fault_id, job_id, attempt,
                tool_id, proposal_key, category, mode, logical_target_id,
                adapter_binding_fingerprint, approval_contract_version, provenance_sha256,
                state, canonical_payload, payload_sha256, approval_sha256,
                proposal_artifact_id, review_summary, created_at_utc, expires_at_utc)
            VALUES (
                @id, @tenant, @report, @fault, @job, 1,
                'ticket_create', @proposal_key, 'ticket_create', 'live', 'ticket:configured-repository',
                @hash, 1, @hash, 'requested', convert_to('{"title":"upgrade proof"}', 'UTF8'),
                @hash, @hash, @artifact, 'Upgrade proof action.',
                clock_timestamp(), clock_timestamp() + interval '1 hour');
            """,
            ("id", actionId),
            ("tenant", origin.TenantId),
            ("report", origin.ReportId),
            ("fault", origin.FaultId),
            ("job", origin.JobId),
            ("proposal_key", proposalKey),
            ("hash", new string('a', 64)),
            ("artifact", origin.EvidenceArtifactId));
        return actionId;
    }

    public static async Task AssertPreProjectionActionPreservedAsync(
        string connectionString,
        Guid actionId)
    {
        Assert.Equal(1, Convert.ToInt64(await ActionApprovalTestSupport.ScalarAsync(
            connectionString,
            "SELECT count(*) FROM incidentcompass.action_approvals WHERE id = @id;",
            ("id", actionId)), CultureInfo.InvariantCulture));
        Assert.Equal(0, Convert.ToInt64(await ActionApprovalTestSupport.ScalarAsync(connectionString, """
            SELECT count(*)
            FROM incidentcompass.action_approvals
            WHERE id = @id
              AND (external_resource_kind IS NOT NULL OR external_resource_id IS NOT NULL OR
                   external_before_state IS NOT NULL OR external_after_state IS NOT NULL);
            """, ("id", actionId)), CultureInfo.InvariantCulture));
    }

    public static async Task InsertReleasedDurableRowsAsync(string connectionString)
    {
        const string sql = """
            INSERT INTO incidentcompass.triage_config_snapshots (
                config_hash, serialized_config, instructions, created_at_utc)
            VALUES ('released-config', '{}'::jsonb, '{}'::jsonb, clock_timestamp());

            INSERT INTO incidentcompass.signals (
                id, tenant_id, source, fingerprint, fingerprint_version, fingerprint_strength,
                service_name, environment, error_type, summary, body, observed_at_utc, received_at_utc)
            VALUES (
                '11111111-1111-1111-1111-111111111111', 'tenant-a', 'released-test',
                'released-fingerprint', 1, 'strong', 'orders', 'test', 'ReleasedException',
                'released signal', '{}'::jsonb, clock_timestamp(), clock_timestamp());

            INSERT INTO incidentcompass.faults (
                id, trigger_signal_id, tenant_id, status, fingerprint, fingerprint_version,
                fingerprint_strength, service_name, environment, created_at_utc)
            VALUES (
                '22222222-2222-2222-2222-222222222222',
                '11111111-1111-1111-1111-111111111111', 'tenant-a', 'Completed',
                'released-fingerprint', 1, 'strong', 'orders', 'test', clock_timestamp());

            UPDATE incidentcompass.signals
            SET fault_id = '22222222-2222-2222-2222-222222222222'
            WHERE id = '11111111-1111-1111-1111-111111111111';

            INSERT INTO incidentcompass.triage_jobs (
                id, fault_id, status, attempt, config_hash, created_at_utc, updated_at_utc)
            VALUES (
                '33333333-3333-3333-3333-333333333333',
                '22222222-2222-2222-2222-222222222222', 'Succeeded', 1,
                'released-config', clock_timestamp(), clock_timestamp());

            INSERT INTO incidentcompass.triage_artifacts (
                id, job_id, attempt, kind, redacted_payload, content_hash, created_at_utc)
            VALUES (
                '44444444-4444-4444-4444-444444444444',
                '33333333-3333-3333-3333-333333333333', NULL, 'TriggerSignal',
                '{}'::jsonb, 'released-artifact', clock_timestamp());

            INSERT INTO incidentcompass.triage_ledger (
                fault_id, job_id, attempt, event_type, rationale, config_hash, created_at_utc)
            VALUES (
                '22222222-2222-2222-2222-222222222222',
                '33333333-3333-3333-3333-333333333333', 1, 'ModelCall',
                '{"routeId":"released-safe-route","provider":"released-provider","model":"released-model","usageSource":"provider","inputTokens":10,"outputTokens":20,"totalTokens":30}',
                'released-config', clock_timestamp());

            INSERT INTO incidentcompass.ai_model_pricing (
                id, provider, model, currency, input_token_price_per_million,
                output_token_price_per_million, effective_from_utc, effective_to_utc)
            VALUES (
                '99999999-9999-9999-9999-999999999999',
                'released-provider', 'released-model', 'USD', 1, 2,
                '2026-01-01T00:00:00Z', NULL);

            INSERT INTO incidentcompass.triage_reports (
                id, fault_id, status, summary, classification, confidence, config_hash, created_at_utc)
            VALUES (
                '55555555-5555-5555-5555-555555555555',
                '22222222-2222-2222-2222-222222222222', 'Completed', 'released report',
                'KnownIncident', 'High', 'released-config', clock_timestamp());

            INSERT INTO incidentcompass.triage_evidence (
                id, report_id, kind, artifact_id, reference, created_at_utc)
            VALUES (
                '66666666-6666-6666-6666-666666666666',
                '55555555-5555-5555-5555-555555555555', 'TriggerSignal',
                '44444444-4444-4444-4444-444444444444', 'released-artifact', clock_timestamp());

            INSERT INTO incidentcompass.memory_items (
                id, tenant_id, kind, source, title, content, content_hash, version, created_at_utc)
            VALUES (
                '77777777-7777-7777-7777-777777777777', 'tenant-a', 'runbook',
                'runbooks/released.md', 'Released runbook', 'Retained durable memory item.',
                'released-memory', 1, clock_timestamp());

            INSERT INTO incidentcompass.memory_chunks (
                id, memory_item_id, tenant_id, chunk_position, text, text_hash,
                embedding_provider, embedding_model, embedding_dimensions,
                embedding_values, embedding_vector, created_at_utc)
            VALUES (
                '88888888-8888-8888-8888-888888888888',
                '77777777-7777-7777-7777-777777777777', 'tenant-a', 0,
                'Retained durable memory chunk.', 'released-memory-chunk',
                'mock', 'test', 2, ARRAY[0.1, 0.2]::real[], '[0.1,0.2]'::vector,
                clock_timestamp());
            """;
        await PostgresMigrationTestSupport.ExecuteAsync(connectionString, sql);
    }

    public static async Task<IReadOnlyList<string>> ReadSchemaSignatureAsync(string connectionString)
    {
        const string sql = """
            SELECT 'index:' || indexname
            FROM pg_indexes
            WHERE schemaname = 'incidentcompass'
            UNION ALL
            SELECT 'constraint:' || conname
            FROM pg_constraint
            WHERE connamespace = 'incidentcompass'::regnamespace
            ORDER BY 1;
            """;
        return await PostgresMigrationTestSupport.ReadStringsAsync(connectionString, sql);
    }

    public static async Task<bool> HasRequiredV02IndexesAndColumnsAsync(string connectionString)
    {
        const string sql = """
            SELECT (
                (SELECT count(*)
                 FROM pg_indexes
                 WHERE schemaname = 'incidentcompass'
                   AND indexname IN (
                       'ux_memory_items_active_seed_owner_source',
                       'ux_memory_items_seed_owner_content',
                       'ix_memory_items_active_lookup',
                       'ix_memory_items_seed_owner_generation')) = 4
                AND
                (SELECT count(*)
                 FROM information_schema.columns
                 WHERE table_schema = 'incidentcompass'
                   AND table_name = 'memory_items'
                   AND column_name IN (
                       'service_name', 'component', 'release_name', 'is_active',
                       'seed_managed', 'updated_at_utc', 'superseded_at_utc',
                       'seed_owner', 'seed_generation')) = 9
                AND
                (SELECT count(*)
                 FROM information_schema.columns
                 WHERE table_schema = 'incidentcompass'
                   AND table_name IN ('signals', 'faults')
                   AND column_name IN ('grouping_rule_id', 'grouping_rule_version')) = 4
                AND
                (SELECT count(*)
                 FROM information_schema.columns
                 WHERE table_schema = 'incidentcompass'
                   AND table_name = 'signals'
                   AND column_name IN ('suppression_rule_id', 'effective_suppression_window_minutes')) = 2
                AND
                (SELECT count(*)
                 FROM pg_indexes
                 WHERE schemaname = 'incidentcompass'
                   AND indexname IN (
                       'ux_faults_open_group',
                       'ix_faults_versioned_group_lookup',
                       'ix_signals_versioned_neighbor_lookup',
                       'ix_signals_suppression_audit',
                       'ix_recurrence_states_escalation_intent')) = 5
                AND
                (SELECT count(*)
                 FROM information_schema.columns
                 WHERE table_schema = 'incidentcompass'
                   AND table_name = 'triage_reports'
                   AND column_name IN ('job_id', 'supersedes_report_id')) = 2
                AND
                (SELECT count(*)
                 FROM pg_indexes
                 WHERE schemaname = 'incidentcompass'
                   AND indexname IN (
                       'ux_triage_reports_job',
                       'ux_triage_reports_supersedes',
                       'ix_triage_reports_fault_history')) = 3
                AND
                EXISTS (
                    SELECT 1
                    FROM information_schema.tables
                    WHERE table_schema = 'incidentcompass'
                      AND table_name = 'recurrence_states')
                AND
                (SELECT count(*)
                 FROM pg_constraint
                 WHERE connamespace = 'incidentcompass'::regnamespace
                   AND conname IN (
                       'ck_triage_artifacts_kind', 'ck_triage_evidence_kind',
                       'ck_triage_reports_no_self_supersede')
                   AND (pg_get_constraintdef(oid) LIKE '%RecurrenceState%'
                        OR conname = 'ck_triage_reports_no_self_supersede')) = 3
                AND
                (SELECT count(*)
                 FROM information_schema.columns
                 WHERE table_schema = 'incidentcompass'
                   AND table_name = 'triage_jobs'
                   AND column_name IN (
                       'retriage_trigger_job_id', 'supersedes_report_id',
                       'retry_without_consuming_attempt')) = 3
                AND
                (SELECT count(*)
                 FROM pg_indexes
                 WHERE schemaname = 'incidentcompass'
                   AND indexname IN (
                       'ux_triage_jobs_retriage_trigger',
                       'ix_triage_reports_created_desc',
                       'ix_faults_tenant_id')) = 3
                AND
                EXISTS (
                    SELECT 1
                    FROM pg_trigger
                    WHERE tgrelid = 'incidentcompass.triage_reports'::regclass
                      AND tgname = 'trg_triage_reports_immutable')
                AND
                (SELECT count(*)
                 FROM pg_constraint
                 WHERE connamespace = 'incidentcompass'::regnamespace
                 AND conname IN (
                       'uq_triage_reports_id_fault',
                       'fk_triage_reports_superseded_same_fault')) = 2
                AND
                (SELECT count(*)
                 FROM information_schema.tables
                 WHERE table_schema = 'incidentcompass'
                   AND table_name IN ('action_approvals', 'action_approval_provenance')) = 2
                AND
                (SELECT count(*)
                 FROM pg_indexes
                 WHERE schemaname = 'incidentcompass'
                   AND indexname IN (
                       'ix_action_approvals_tenant_created',
                       'ix_action_approvals_dispatch_candidates',
                       'ix_action_approvals_external_resource')) = 3
                AND
                (SELECT count(*)
                 FROM information_schema.columns
                 WHERE table_schema = 'incidentcompass'
                   AND table_name = 'action_approvals'
                   AND column_name IN (
                       'external_resource_kind', 'external_resource_id',
                       'external_before_state', 'external_after_state')) = 4
                AND
                (SELECT count(*)
                 FROM pg_constraint
                 WHERE connamespace = 'incidentcompass'::regnamespace
                   AND conname IN (
                       'ck_action_approvals_external_resource_kind',
                       'ck_action_approvals_external_resource_id',
                       'ck_action_approvals_external_before_state_bound',
                       'ck_action_approvals_external_after_state_bound',
                       'ck_action_approvals_external_projection_shape',
                       'ck_action_approvals_external_projection_transition')) = 6
                AND
                (SELECT count(*)
                 FROM pg_trigger
                 WHERE NOT tgisinternal
                   AND tgname IN (
                       'trg_action_approvals_immutable',
                       'trg_action_approvals_no_delete',
                       'trg_action_approval_provenance_sealed',
                       'trg_action_approval_provenance_immutable',
                       'trg_action_review_artifact_immutable')) = 5
                AND
                (SELECT count(*)
                 FROM pg_constraint
                 WHERE connamespace = 'incidentcompass'::regnamespace
                   AND conname IN (
                       'ck_triage_artifacts_kind',
                       'triage_ledger_event_type_check',
                       'triage_ledger_decision_check',
                       'triage_ledger_tool_status_check',
                       'triage_ledger_status_shape_check',
                       'triage_ledger_action_ref_check')) = 6
                AND
                EXISTS (
                    SELECT 1
                    FROM pg_indexes
                    WHERE schemaname = 'incidentcompass'
                      AND indexname = 'ix_triage_ledger_model_call_fault_created_at'
                      AND indexdef LIKE '%(fault_id, created_at_utc)%'
                      AND indexdef LIKE '%event_type%ModelCall%')
                AND
                EXISTS (
                    SELECT 1
                    FROM information_schema.tables
                    WHERE table_schema = 'incidentcompass'
                      AND table_name = 'post_report_action_intents')
                AND
                EXISTS (
                    SELECT 1
                    FROM pg_indexes
                    WHERE schemaname = 'incidentcompass'
                      AND indexname = 'ix_post_report_action_intents_candidates')
                AND
                (SELECT count(*)
                 FROM pg_trigger
                 WHERE NOT tgisinternal
                   AND tgname IN (
                       'trg_post_report_action_intents_transition',
                       'trg_post_report_action_intents_no_delete')) = 2
                AND
                EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid = 'incidentcompass.triage_artifacts'::regclass
                      AND conname = 'ck_triage_artifacts_kind'
                      AND pg_get_constraintdef(oid) LIKE '%ProposedAction%'
                      AND pg_get_constraintdef(oid) LIKE '%ActionResult%')
            );
            """;
        return Convert.ToBoolean(await PostgresMigrationTestSupport.ExecuteScalarAsync(connectionString, sql), CultureInfo.InvariantCulture);
    }

    public static async Task AssertReportRowsAreImmutableAsync(string connectionString)
    {
        var exception = await Record.ExceptionAsync(() => PostgresMigrationTestSupport.ExecuteAsync(
            connectionString,
            "UPDATE incidentcompass.triage_reports SET summary = 'mutated' WHERE id = '55555555-5555-5555-5555-555555555555';"));

        var postgresException = Assert.IsType<PostgresException>(exception);
        Assert.Contains("immutable", postgresException.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The price administration rails reach a database the same way whether the schema was built
    /// from nothing or upgraded onto rows an earlier release wrote.
    /// </summary>
    public static async Task AssertModelPriceAdministrationAppliedAsync(string connectionString)
    {
        // The five prices the schema seeded name the schema. Rows an operator wrote before this
        // version stay NULL rather than being given a borrowed author.
        Assert.Equal(5, await PostgresMigrationTestSupport.CountSqlAsync(connectionString, """
            SELECT count(*) FROM incidentcompass.ai_model_pricing
            WHERE administered_by = 'schema:004-observability-cost.sql'
              AND administered_at_utc IS NOT NULL;
            """));

        var deleted = await Record.ExceptionAsync(() => PostgresMigrationTestSupport.ExecuteAsync(
            connectionString,
            "DELETE FROM incidentcompass.ai_model_pricing WHERE provider = 'mock';"));
        Assert.Contains(
            "retired by setting effective_to_utc",
            Assert.IsType<PostgresException>(deleted).MessageText,
            StringComparison.Ordinal);

        var unattributed = await Record.ExceptionAsync(() => PostgresMigrationTestSupport.ExecuteAsync(
            connectionString,
            """
            INSERT INTO incidentcompass.ai_model_pricing (
                id, provider, model, currency, input_token_price_per_million,
                output_token_price_per_million, effective_from_utc)
            VALUES (gen_random_uuid(), 'migration-check', 'migration-model', 'USD', 1, 2,
                '2026-01-01T00:00:00Z');
            """));
        Assert.Contains(
            "must name who changed it",
            Assert.IsType<PostgresException>(unattributed).MessageText,
            StringComparison.Ordinal);

        await PostgresMigrationTestSupport.ExecuteAsync(connectionString, """
            INSERT INTO incidentcompass.ai_model_pricing (
                id, provider, model, currency, input_token_price_per_million,
                output_token_price_per_million, effective_from_utc, administered_by)
            VALUES (gen_random_uuid(), 'migration-check', 'migration-model', 'USD', 1, 2,
                '2026-01-01T00:00:00Z', 'test:migration');
            """);
        var overlapping = await Record.ExceptionAsync(() => PostgresMigrationTestSupport.ExecuteAsync(
            connectionString,
            """
            INSERT INTO incidentcompass.ai_model_pricing (
                id, provider, model, currency, input_token_price_per_million,
                output_token_price_per_million, effective_from_utc, administered_by)
            VALUES (gen_random_uuid(), 'migration-check', 'migration-model', 'USD', 3, 4,
                '2026-02-01T00:00:00Z', 'test:migration');
            """));
        Assert.Equal(
            "ex_ai_model_pricing_no_overlap",
            Assert.IsType<PostgresException>(overlapping).ConstraintName);
    }

    public static async Task<MigrationCostRollupHistory> SeedCostRollupHistoryAsync(
        string connectionString,
        string suffix)
    {
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(
            connectionString,
            "cost-upgrade-" + suffix);
        var priceId = Guid.NewGuid();
        var routeId = "upgrade-safe-route-" + suffix;
        await ActionApprovalTestSupport.ExecuteAsync(connectionString, """
            INSERT INTO incidentcompass.ai_model_pricing (
                id, provider, model, currency, input_token_price_per_million,
                output_token_price_per_million, effective_from_utc, effective_to_utc)
            VALUES (@price, @provider, @model, 'USD', 1, 2,
                '2026-01-01T00:00:00Z', NULL);
            INSERT INTO incidentcompass.triage_ledger (
                fault_id, job_id, attempt, event_type, rationale, config_hash, created_at_utc)
            VALUES (@fault, @job, 1, 'ModelCall', @rationale, @config,
                '2026-08-01T00:30:00Z');
            """,
            ("price", priceId), ("provider", "upgrade-provider-" + suffix),
            ("model", "upgrade-model-" + suffix), ("fault", origin.FaultId),
            ("job", origin.JobId), ("config", origin.ConfigHash),
            ("rationale", "{\"routeId\":\"" + routeId +
                "\",\"provider\":\"upgrade-provider-" + suffix +
                "\",\"model\":\"upgrade-model-" + suffix +
                "\",\"usageSource\":\"provider\",\"inputTokens\":10,\"outputTokens\":20,\"totalTokens\":30}"));
        return new MigrationCostRollupHistory(priceId, origin.JobId, routeId);
    }

    public static async Task AssertCostRollupHistoryPreservedAsync(
        string connectionString,
        MigrationCostRollupHistory history)
    {
        Assert.Equal(1, await PostgresMigrationTestSupport.CountSqlAsync(connectionString, """
            SELECT count(*) FROM incidentcompass.ai_model_pricing WHERE id = @price;
            """, ("price", history.PriceId)));
        Assert.Equal(1, await PostgresMigrationTestSupport.CountSqlAsync(connectionString, """
            SELECT count(*) FROM incidentcompass.triage_ledger
            WHERE job_id = @job AND event_type = 'ModelCall' AND rationale LIKE '%' || @route || '%';
            """, ("job", history.JobId), ("route", history.RouteId)));
    }
}
