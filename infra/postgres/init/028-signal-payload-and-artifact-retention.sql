-- Schema for the two retention operations: compacting aged raw signal payloads, and reaping the
-- artifacts of attempts that are no longer a job's current attempt.
--
-- Neither operation deletes a signal row or a report. `faults.trigger_signal_id` has a foreign key
-- to `signals` (007-intake.sql), so a signal row cannot be removed without breaking the fault that
-- the signal opened, and `trg_triage_reports_immutable` (018-report-lifecycle.sql) rejects UPDATE
-- and DELETE on `triage_reports` unconditionally. Retention here is therefore payload-shaped, not
-- record-shaped.

-- The authoritative claim that retention emptied `signals.attributes` and `signals.body`. It has to
-- be a column of its own: both payload columns are `jsonb NOT NULL` and `attributes` defaults to
-- '{}', so an emptied payload and a signal that genuinely arrived with no attributes are the same
-- bytes, and a sentinel written inside the payload would be something a connector could also write.
-- The backend sets this column at the moment it empties the row, so it is not something source text
-- can assert. NULL means retention never touched this row, which is deliberately not the same claim
-- as "arrived empty".
ALTER TABLE incidentcompass.signals
    ADD COLUMN IF NOT EXISTS payload_compacted_at_utc timestamptz NULL;

-- The compaction scan ages rows by `received_at_utc`, never by `observed_at_utc`: the observed time
-- arrives inside the signal, so a source could claim an ancient time to have its payload dropped at
-- once, or a future one to stay out of retention forever. `received_at_utc` is written by intake.
-- The partial predicate is what makes a second run over the same window cheap and what makes the
-- operation idempotent: a compacted row leaves the index.
--
-- The whole compaction predicate lives on this one table, so this index serves the scan end to end
-- and a run really does stop at its row budget. Measured on PostgreSQL 16 over 300,001 signals with
-- 275,085 of them past a 30-day cutoff and a 500-row budget: index scan, 1,005 buffers, 3.5 ms,
-- stopping at the 500th row. The reap below is deliberately not described the same way.
CREATE INDEX IF NOT EXISTS ix_signals_compaction_candidates
    ON incidentcompass.signals (received_at_utc, id)
    WHERE payload_compacted_at_utc IS NULL;

-- The reap index puts `created_at_utc` first so a run can walk attempt-level artifact rows oldest
-- first, and `attempt IS NULL` is the job-level sentinel documented in 007-intake.sql and is never
-- reapable, so it is the index predicate rather than a filter applied after the scan.
--
-- What this index cannot do is make a run's cost proportional to its row budget, and no index on
-- this table can. The selector the reap actually turns on is "this artifact's attempt is not its
-- job's current attempt", which is a comparison against `triage_jobs.attempt` and therefore not a
-- fact any index on `triage_artifacts` can hold. Measured on PostgreSQL 16 over 285,050 artifacts
-- across 30,000 jobs, 279,580 of them past the cutoff, with a 500-row budget and the default 4 MB
-- `work_mem`:
--
--   * when stale rows are common (around 2% of the table), the planner does use this index. It walks
--     21,724 entries oldest first and probes 15,440 jobs to fill the budget: 70,235 buffers, 47 ms.
--     Dropping the index turns the same query into a sequential scan and a sort at 115 ms, which is
--     why it is worth having.
--   * when stale rows are rare (around 0.2%), the planner does not use it. It sequentially scans all
--     279,532 qualifying rows, hash joins `triage_jobs` and sorts: 28,521 buffers, 148 ms. Forcing
--     the index with `enable_seqscan = off` is worse, not better - 226,523 entries walked, 320,978
--     buffers, 596 ms - so this is the planner being right rather than statistics to tune away.
--
-- Both plans read work proportional to the table. The row budget therefore bounds what one run
-- deletes and how long it holds locks, not how much it reads, and a first run over a long-lived
-- database is a full-table read whichever plan it gets. A composite
-- `(job_id, attempt, created_at_utc) WHERE attempt IS NOT NULL` was built and measured against both
-- shapes and was never chosen, in either, even with sequential scans disabled, so the simpler index
-- is the one that ships.
--
-- There is no index-level guard for the other exclusions, and one of them cannot be a database
-- constraint at all: `action_approval_provenance.source_id` (023-action-approvals-outbox.sql)
-- references either a report or an artifact depending on `source_type`, so it carries no foreign
-- key and nothing in PostgreSQL would refuse to orphan it or report that it had been orphaned. The
-- reap statement excludes those rows explicitly instead.
CREATE INDEX IF NOT EXISTS ix_triage_artifacts_reap_candidates
    ON incidentcompass.triage_artifacts (created_at_utc, id)
    WHERE attempt IS NOT NULL;
