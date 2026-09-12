-- Makes the hand-edited model price table safe to administer.
--
-- `incidentcompass.ai_model_pricing` has no writer in this system. There is no price API, no CLI
-- and no configuration key that produces a row, and adding an HTTP surface would need an admin
-- identity this codebase does not have: every API identity is tenant-scoped, while a price is
-- host-global. So the writer is an operator at a `psql` prompt, and the honest response is to make
-- that safe rather than to pretend it will not happen. Everything below constrains the hand-edit.
--
-- Three columns and two triggers, each answering a different way a hand-edit goes wrong.
--
-- 1. An unattributed change. A price decides what an hour of model usage cost, and the rollup
--    re-reads the table on every request, so a row edited today changes the answer given for a
--    window that closed last month. `administered_by` is operator-asserted free text and is
--    deliberately not called an actor, a user or a principal: nothing authenticates it, and a
--    column claiming an authenticated identity where none exists would be worth less than an
--    honest label. `administered_at_utc` is the opposite - it is stamped by the database on every
--    insert and update and the supplied value is discarded, so the one field an operator cannot
--    quietly backdate is the timestamp. `administration_note` is where the reason lives, which
--    matters most for the case that rewrites history: correcting a price that was always wrong.
--
--    These columns record the latest change, not a history of changes. Two successive corrections
--    leave only the second one's attribution. The runbook therefore prescribes closing an interval
--    and opening a new one for a price that changes going forward, which keeps each row's own
--    author intact and leaves in-place editing for genuine corrections. A full audit table is not
--    built here: it would be a second record of the same fact, and the delete refusal below
--    already keeps the row itself from disappearing.
--
--    Existing rows keep NULL. Rows written before this migration were written by nobody this
--    schema can name, and filling in a placeholder would assert something never observed. The five
--    prices seeded by `004-observability-cost.sql` are the exception and are backfilled, because
--    their author is known exactly: the shipped schema wrote them.
--
-- 2. Two intervals covering one instant for the same provider and model. The rollup already treats
--    that as ambiguous and leaves the call unpriced, which is the right thing to do at read time -
--    it refuses to arbitrate between two prices rather than picking one. But silently dropping the
--    spend is a poor way to learn about a typo in a date, and the operator who made it sees only a
--    number that is too low. The exclusion constraint moves the failure to the moment of the
--    mistake, where it can be fixed, and leaves the read-side fail-closed behaviour untouched as
--    the second line of defence for a database restored from before this version.
--
--    The range is `[from, to)`, half-open, which is exactly the interval the rollup applies:
--    effective from the instant inclusive, until the instant exclusive. An interval that ends
--    where the next begins therefore does not overlap, which is the normal shape of a price
--    change. Provider and model compare with plain equality, matching the case-sensitive lookup
--    the rollup performs.
--
--    An upgrade of a database that already holds overlapping rows fails here, deliberately and
--    with the conflicting pairs named. The alternative would be to silently pick a row to close,
--    which is the arbitration the read path refuses to do.
--
-- 3. A deleted price. A delete is the one edit whose damage cannot be seen afterwards: the row is
--    gone, the hour it priced quietly becomes unpriced, and a spend figure already reported to
--    someone drops with nothing left in the database to explain why. `triage_reports` and
--    `action_approvals` already refuse destructive changes for the same reason - a durable claim
--    that can stop being true is not a claim - and a price is the input to a figure of the same
--    kind. Retirement is a real need and it has a non-destructive form: set `effective_to_utc`.
--    That stops the price applying to anything after that instant and leaves every hour before it
--    priced exactly as it was reported. Updates stay allowed, because correcting a wrong price is
--    legitimate and now leaves a name, a time and a reason behind it.

CREATE EXTENSION IF NOT EXISTS btree_gist;

ALTER TABLE incidentcompass.ai_model_pricing
    ADD COLUMN IF NOT EXISTS administered_by text NULL,
    ADD COLUMN IF NOT EXISTS administration_note text NULL,
    ADD COLUMN IF NOT EXISTS administered_at_utc timestamptz NULL;

DO $migration$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'incidentcompass.ai_model_pricing'::regclass
          AND conname = 'ck_ai_model_pricing_administered_by') THEN
        ALTER TABLE incidentcompass.ai_model_pricing
            ADD CONSTRAINT ck_ai_model_pricing_administered_by CHECK (
                administered_by IS NULL OR
                length(btrim(administered_by)) BETWEEN 1 AND 256);
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'incidentcompass.ai_model_pricing'::regclass
          AND conname = 'ck_ai_model_pricing_administration_note') THEN
        ALTER TABLE incidentcompass.ai_model_pricing
            ADD CONSTRAINT ck_ai_model_pricing_administration_note CHECK (
                administration_note IS NULL OR
                (length(btrim(administration_note)) > 0 AND
                 octet_length(administration_note) <= 2048));
    END IF;

    -- A note or a change time without a name is attribution that names nobody, and a name without
    -- a change time is a claim with no moment attached. The trigger below always writes both.
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'incidentcompass.ai_model_pricing'::regclass
          AND conname = 'ck_ai_model_pricing_administration_shape') THEN
        ALTER TABLE incidentcompass.ai_model_pricing
            ADD CONSTRAINT ck_ai_model_pricing_administration_shape CHECK (
                (administered_by IS NULL AND administered_at_utc IS NULL AND
                 administration_note IS NULL) OR
                (administered_by IS NOT NULL AND administered_at_utc IS NOT NULL));
    END IF;
END $migration$;

UPDATE incidentcompass.ai_model_pricing
SET administered_by = 'schema:004-observability-cost.sql',
    administered_at_utc = created_at_utc,
    administration_note = 'Seeded with the schema so a mock-provider run reads as zero cost.'
WHERE administered_by IS NULL
  AND id IN (
      '00000000-0000-0000-0000-000000000501',
      '00000000-0000-0000-0000-000000000502',
      '00000000-0000-0000-0000-000000000503',
      '00000000-0000-0000-0000-000000000504',
      '00000000-0000-0000-0000-000000000505');

DO $migration$
DECLARE
    conflicting_pairs text;
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'incidentcompass.ai_model_pricing'::regclass
          AND conname = 'ex_ai_model_pricing_no_overlap') THEN
        RETURN;
    END IF;

    SELECT string_agg(pair, ', ' ORDER BY pair)
    INTO conflicting_pairs
    FROM (
        SELECT DISTINCT format('%s/%s', earlier.provider, earlier.model) AS pair
        FROM incidentcompass.ai_model_pricing AS earlier
        JOIN incidentcompass.ai_model_pricing AS later
          ON later.provider = earlier.provider
         AND later.model = earlier.model
         AND later.id <> earlier.id
         AND tstzrange(earlier.effective_from_utc, earlier.effective_to_utc) &&
             tstzrange(later.effective_from_utc, later.effective_to_utc)
    ) AS conflicts;

    IF conflicting_pairs IS NOT NULL THEN
        RAISE EXCEPTION
            'incidentcompass.ai_model_pricing holds overlapping effective intervals for provider/model: %. '
            'Close or correct one interval of each pair by hand, then rerun the upgrade. '
            'These rows are already unpriced in the cost rollup, so closing them changes no reported figure.',
            conflicting_pairs;
    END IF;

    ALTER TABLE incidentcompass.ai_model_pricing
        ADD CONSTRAINT ex_ai_model_pricing_no_overlap EXCLUDE USING gist (
            provider WITH =,
            model WITH =,
            tstzrange(effective_from_utc, effective_to_utc) WITH &&);
END $migration$;

CREATE OR REPLACE FUNCTION incidentcompass.record_ai_model_pricing_administration()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    -- An insert naming an id that is already present is not a new price. It is either discarded by
    -- an ON CONFLICT clause or rejected by the primary key, and it never becomes a row either way,
    -- so the attribution rule has nothing to attach to. Letting it through is what keeps every
    -- script under infra/postgres/init re-appliable: 004-observability-cost.sql re-inserts its five
    -- seed prices with ON CONFLICT (id) DO NOTHING, and a rule that refused those would turn a
    -- deliberately idempotent script into one that can only be run once.
    IF TG_OP = 'INSERT' AND EXISTS (
        SELECT 1 FROM incidentcompass.ai_model_pricing WHERE id = NEW.id) THEN
        RETURN NEW;
    END IF;

    IF NEW.administered_by IS NULL OR length(btrim(NEW.administered_by)) = 0 THEN
        RAISE EXCEPTION
            'a model price row must name who changed it: set administered_by to the operator making this change';
    END IF;

    -- Stamped, never accepted. The supplied value is discarded so the recorded moment of a change
    -- is the database clock rather than whatever the editing statement claimed.
    NEW.administered_at_utc := clock_timestamp();

    IF TG_OP = 'UPDATE' THEN
        -- When the row first appeared is a fact about the past that an edit does not get to move.
        NEW.created_at_utc := OLD.created_at_utc;
    END IF;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_ai_model_pricing_administration ON incidentcompass.ai_model_pricing;

CREATE TRIGGER trg_ai_model_pricing_administration
    BEFORE INSERT OR UPDATE ON incidentcompass.ai_model_pricing
    FOR EACH ROW
    EXECUTE FUNCTION incidentcompass.record_ai_model_pricing_administration();

CREATE OR REPLACE FUNCTION incidentcompass.reject_ai_model_pricing_delete()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION
        'model prices are retired by setting effective_to_utc, not deleted: '
        'deleting one silently changes what an already-reported hour cost';
END;
$$;

DROP TRIGGER IF EXISTS trg_ai_model_pricing_no_delete ON incidentcompass.ai_model_pricing;

CREATE TRIGGER trg_ai_model_pricing_no_delete
    BEFORE DELETE ON incidentcompass.ai_model_pricing
    FOR EACH ROW
    EXECUTE FUNCTION incidentcompass.reject_ai_model_pricing_delete();
