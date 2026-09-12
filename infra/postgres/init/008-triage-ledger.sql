-- Durable triage ledger. Events are appended as they occur; ordering is assigned by
-- PostgreSQL identity, never by application-side MAX(id)+1 logic.

CREATE TABLE IF NOT EXISTS incidentcompass.triage_ledger (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    fault_id uuid NOT NULL REFERENCES incidentcompass.faults (id),
    job_id uuid NOT NULL REFERENCES incidentcompass.triage_jobs (id),
    attempt integer NOT NULL CHECK (attempt > 0),
    event_type text NOT NULL CHECK (event_type IN (
        'Delegated',
        'ToolProposed',
        'PolicyDecision',
        'ToolResult',
        'WorkerCompleted',
        'BudgetEvent',
        'ReportPublished',
        'ModelCall')),
    role text NULL,
    tool_name text NULL,
    rationale text NULL,
    decision text NULL CHECK (decision IS NULL OR decision IN ('Allowed', 'Denied', 'ApprovalRequired')),
    decision_reason text NULL,
    tool_status text NULL CHECK (tool_status IS NULL OR tool_status IN ('Succeeded', 'Failed')),
    tokens_delta integer NULL,
    workers_delta integer NULL,
    payload_ref text NULL,
    config_hash text NOT NULL REFERENCES incidentcompass.triage_config_snapshots (config_hash),
    created_at_utc timestamptz NOT NULL,
    CHECK ((event_type = 'ToolResult' AND tool_status IS NOT NULL) OR (event_type <> 'ToolResult' AND tool_status IS NULL)),
    CHECK (event_type = 'BudgetEvent' OR (tokens_delta IS NULL AND workers_delta IS NULL))
);

CREATE INDEX IF NOT EXISTS ix_triage_ledger_job_order
    ON incidentcompass.triage_ledger (job_id, id);

CREATE INDEX IF NOT EXISTS ix_triage_ledger_fault_order
    ON incidentcompass.triage_ledger (fault_id, id);
