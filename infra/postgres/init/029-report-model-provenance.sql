-- What a published report can say about the models that produced it.
--
-- One investigation makes many model calls: one or more orchestrator turns, one worker turn per
-- delegated role plus that worker's own tool and correction turns, and any orchestrator reprompt
-- turns. A role names its own route, so those calls can run on different routes, different
-- providers and different models within a single attempt. There is therefore no single "the model
-- that wrote this report", and a column holding one model name would be a false claim the moment a
-- worker role answered on a different route than the orchestrator - which is the default shape of
-- the shipped configuration, not an exotic case.
--
-- So this column holds a list, not a name. Each element is one distinct combination of call kind,
-- role, route, provider and model that answered during the attempt that published this report,
-- with how many calls it answered, in the order each combination first answered. Two models used
-- for the same role are two elements, so the claim stays true if a later change lets one role run
-- on more than one route.
--
-- The value is derived inside the publish transaction from this job attempt's own `ModelCall`
-- triage-ledger rows, which record what actually answered rather than what was asked for. It is a
-- projection of the ledger with exactly one producer, not a second accumulator kept in step by
-- hand. It lives on the report rather than only in the ledger because a report is immutable and
-- never deleted (018-report-lifecycle.sql) while ledger rows carry no such guarantee, and a report
-- that could stop being able to name its own models is not a durable claim. The `ReportPublished`
-- ledger row is deliberately not given a second copy: it already points at the report through
-- `payload_ref`.
--
-- The three values this column can take are three different claims, and none of them stands in for
-- another:
--
--   * NULL       - this report was published before provenance was recorded. Reports are immutable,
--                  so these rows cannot be backfilled, and writing an empty array over them would
--                  assert something about them that was never observed.
--   * '[]'       - provenance was derived for this report and the attempt recorded no model call.
--                  A governed investigation always records at least the orchestrator turn that
--                  called publish_report, because a model call whose ledger append fails takes the
--                  whole attempt down with it, so this is the shape of a report published through
--                  a path that consulted no model at all.
--   * a non-empty
--     array      - these are the models that answered.
ALTER TABLE incidentcompass.triage_reports
    ADD COLUMN IF NOT EXISTS model_provenance jsonb NULL;

ALTER TABLE incidentcompass.triage_reports
    DROP CONSTRAINT IF EXISTS ck_triage_reports_model_provenance_shape;

ALTER TABLE incidentcompass.triage_reports
    ADD CONSTRAINT ck_triage_reports_model_provenance_shape
        CHECK (model_provenance IS NULL OR jsonb_typeof(model_provenance) = 'array');
