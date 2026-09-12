-- Report persistence. The final Worker commit writes report + evidence + fault status +
-- job success + ReportPublished in one fenced transaction.

CREATE TABLE IF NOT EXISTS incidentcompass.triage_reports (
    id uuid PRIMARY KEY,
    fault_id uuid NOT NULL REFERENCES incidentcompass.faults (id),
    status text NOT NULL CHECK (status IN ('Completed', 'InsufficientEvidence', 'Failed')),
    summary text NOT NULL CHECK (length(btrim(summary)) > 0),
    classification text NOT NULL CHECK (classification IN ('KnownIncident', 'LikelyRegression', 'SimpleKnownError', 'Unknown', 'Noise')),
    confidence text NOT NULL CHECK (confidence IN ('Low', 'Medium', 'High')),
    is_mass_issue boolean NULL,
    recommended_next_action text NULL,
    limitations text[] NOT NULL DEFAULT ARRAY[]::text[],
    config_hash text NOT NULL REFERENCES incidentcompass.triage_config_snapshots (config_hash),
    created_at_utc timestamptz NOT NULL,
    UNIQUE (fault_id)
);

CREATE TABLE IF NOT EXISTS incidentcompass.triage_evidence (
    id uuid PRIMARY KEY,
    report_id uuid NOT NULL REFERENCES incidentcompass.triage_reports (id) ON DELETE CASCADE,
    kind text NOT NULL CHECK (kind IN ('TriggerSignal', 'NeighborSet', 'PriorReport', 'RetrievedItem', 'Runbook', 'KnownIncident', 'ToolResult')),
    artifact_id uuid NOT NULL REFERENCES incidentcompass.triage_artifacts (id),
    reference text NOT NULL CHECK (length(btrim(reference)) > 0),
    quote text NULL,
    score double precision NULL,
    created_at_utc timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_triage_evidence_report
    ON incidentcompass.triage_evidence (report_id, created_at_utc, id);

CREATE INDEX IF NOT EXISTS ix_triage_evidence_artifact
    ON incidentcompass.triage_evidence (artifact_id);
