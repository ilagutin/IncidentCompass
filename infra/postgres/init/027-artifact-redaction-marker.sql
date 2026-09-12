-- Records whether the redactor actually removed anything on an artifact's way to durable state.
-- The stored payload cannot answer this: a redacted value and connector text that already contained
-- the literal [REDACTED] are the same bytes, so a marker derived by reading the payload could be
-- raised by whoever wrote the ticket or the source file. This column is written by the backend at
-- the moment redaction runs, from a comparison against the pre-redaction document, and is therefore
-- not something connector text can assert.
--
-- NULL means no boundary recorded an outcome for this row: intake-written artifacts are assembled
-- from a signal that intake redacted before the artifact existed, so the outcome is not observable
-- where the row is written. NULL is deliberately not the same claim as false.
ALTER TABLE incidentcompass.triage_artifacts
    ADD COLUMN IF NOT EXISTS redaction_applied boolean;
