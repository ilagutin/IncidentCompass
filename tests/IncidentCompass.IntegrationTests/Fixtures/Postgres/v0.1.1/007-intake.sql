-- Ingestion and intake schema: signals, faults, triage jobs, config snapshots, artifacts.
--
-- `faults.trigger_signal_id` and `signals.fault_id` reference each other. Tables are created in an
-- order that avoids the circularity: `faults` first (trigger_signal_id as a plain column), then
-- `signals` (which can reference `faults` immediately), then an ALTER TABLE adds the deferred FK
-- from `faults.trigger_signal_id` back to `signals`. Application code inserts a signal row with
-- `fault_id = NULL` first, then the fault row, then updates the signal's `fault_id` - see
-- `ISignalRepository.AttachToFaultAsync`.

CREATE TABLE IF NOT EXISTS incidentcompass.faults (
    id uuid PRIMARY KEY,
    trigger_signal_id uuid NOT NULL,
    tenant_id text NOT NULL CHECK (length(btrim(tenant_id)) > 0),
    status text NOT NULL CHECK (status IN ('Queued', 'Analyzing', 'Completed', 'Failed', 'InsufficientEvidence')),
    fingerprint text NOT NULL,
    fingerprint_version integer NOT NULL,
    fingerprint_strength text NOT NULL CHECK (fingerprint_strength IN ('strong', 'weak')),
    can_group boolean GENERATED ALWAYS AS (fingerprint_strength = 'strong') STORED,
    service_name text NOT NULL,
    environment text NOT NULL,
    severity text NULL,
    correlation_id text NULL,
    created_at_utc timestamptz NOT NULL,
    completed_at_utc timestamptz NULL,
    recurrence_of uuid NULL REFERENCES incidentcompass.faults (id),
    CHECK (completed_at_utc IS NULL OR completed_at_utc >= created_at_utc)
);

-- The "one open fault per group" rule applies only to groupable (strong-fingerprint) signals;
-- weak signals are excluded by the predicate so each can open its own fault without colliding.
CREATE UNIQUE INDEX IF NOT EXISTS ux_faults_open_group
    ON incidentcompass.faults (tenant_id, service_name, environment, fingerprint, fingerprint_version)
    WHERE status IN ('Queued', 'Analyzing') AND can_group;

CREATE INDEX IF NOT EXISTS ix_faults_group_lookup
    ON incidentcompass.faults (tenant_id, service_name, environment, fingerprint, fingerprint_version, created_at_utc DESC);

CREATE TABLE IF NOT EXISTS incidentcompass.signals (
    id uuid PRIMARY KEY,
    tenant_id text NOT NULL CHECK (length(btrim(tenant_id)) > 0),
    source text NOT NULL CHECK (length(btrim(source)) > 0),
    fault_id uuid NULL REFERENCES incidentcompass.faults (id),
    fingerprint text NULL,
    fingerprint_version integer NULL,
    fingerprint_strength text NOT NULL DEFAULT 'weak' CHECK (fingerprint_strength IN ('strong', 'weak')),
    can_group boolean GENERATED ALWAYS AS (fingerprint_strength = 'strong') STORED,
    external_id text NULL,
    is_suppressed boolean NOT NULL DEFAULT false,
    suppressed_by_fault_id uuid NULL REFERENCES incidentcompass.faults (id),
    suppression_reason text NULL,
    trace_id text NULL,
    span_id text NULL,
    parent_span_id text NULL,
    service_name text NOT NULL DEFAULT 'unknown',
    environment text NOT NULL DEFAULT 'unknown',
    operation_name text NULL,
    severity text NULL,
    error_type text NULL,
    error_message text NULL,
    summary text NOT NULL CHECK (length(btrim(summary)) > 0),
    description text NULL,
    http_method text NULL,
    http_route text NULL,
    http_status_code integer NULL,
    duration_ms integer NULL,
    attributes jsonb NOT NULL DEFAULT '{}'::jsonb,
    body jsonb NOT NULL,
    observed_at_utc timestamptz NOT NULL,
    received_at_utc timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_signals_fault
    ON incidentcompass.signals (fault_id);

-- Backs the neighbor-count/isMassIssue window query (fingerprint match within a lookback window).
CREATE INDEX IF NOT EXISTS ix_signals_neighbor_lookup
    ON incidentcompass.signals (tenant_id, service_name, environment, fingerprint, fingerprint_version, observed_at_utc);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'fk_faults_trigger_signal'
    ) THEN
        ALTER TABLE incidentcompass.faults
            ADD CONSTRAINT fk_faults_trigger_signal
            FOREIGN KEY (trigger_signal_id) REFERENCES incidentcompass.signals (id);
    END IF;
END;
$$;

CREATE TABLE IF NOT EXISTS incidentcompass.triage_config_snapshots (
    config_hash text PRIMARY KEY,
    serialized_config jsonb NOT NULL,
    instructions jsonb NOT NULL,
    created_at_utc timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS incidentcompass.triage_jobs (
    id uuid PRIMARY KEY,
    fault_id uuid NOT NULL REFERENCES incidentcompass.faults (id),
    status text NOT NULL CHECK (status IN ('Pending', 'Processing', 'Succeeded', 'Failed', 'RetryPending', 'DeadLettered')),
    attempt integer NOT NULL CHECK (attempt > 0),
    locked_by text NULL,
    locked_until_utc timestamptz NULL,
    next_attempt_at_utc timestamptz NULL,
    last_error_code text NULL,
    last_error_message text NULL,
    config_hash text NOT NULL REFERENCES incidentcompass.triage_config_snapshots (config_hash),
    created_at_utc timestamptz NOT NULL,
    updated_at_utc timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_triage_jobs_fault
    ON incidentcompass.triage_jobs (fault_id);

CREATE INDEX IF NOT EXISTS ix_triage_jobs_status_poll
    ON incidentcompass.triage_jobs (status, next_attempt_at_utc);

CREATE TABLE IF NOT EXISTS incidentcompass.triage_artifacts (
    id uuid PRIMARY KEY,
    job_id uuid NOT NULL REFERENCES incidentcompass.triage_jobs (id),
    -- NULL = job-level (intake artifacts: TriggerSignal/NeighborSet/PriorReport, valid across retries);
    -- set = attempt-level (worker artifacts, written by the investigation Worker). NULL is a deliberate sentinel,
    -- not "unknown" - do not make this column NOT NULL.
    attempt integer NULL,
    kind text NOT NULL CHECK (kind IN ('TriggerSignal', 'NeighborSet', 'PriorReport', 'RetrievedItem', 'ToolResult', 'WorkerOutput')),
    domain_ref text NULL,
    redacted_payload jsonb NOT NULL,
    content_hash text NOT NULL,
    created_at_utc timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_triage_artifacts_job_attempt
    ON incidentcompass.triage_artifacts (job_id, attempt);

CREATE UNIQUE INDEX IF NOT EXISTS ux_triage_artifacts_job_level_kind
    ON incidentcompass.triage_artifacts (job_id, kind)
    WHERE attempt IS NULL;
