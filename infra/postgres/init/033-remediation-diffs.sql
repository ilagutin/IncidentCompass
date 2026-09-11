-- Durable remediation diffs: the exact change a post-report remediation pass produced, the tree it
-- was prepared against, the tree it produced, and what was and was not verified about it.
--
-- This is a separate table rather than a `triage_artifacts` row, and the reason is redaction. Every
-- payload written to `triage_artifacts.redacted_payload` passes a redactor on its way in, which is
-- what makes that column safe to read back. A unified diff cannot pass one and remain a diff: a
-- redacted context line no longer matches the base byte for byte, so the change stops applying, and
-- a redacted added line writes a placeholder into source. Putting a diff there would therefore mean
-- either breaking it or exempting it, and an exemption inside the redaction boundary is worse than a
-- table outside it. The rows here are model text about attacker-influenced incident data and must be
-- treated as such by every reader.
--
-- Where the diff body lives is settled here as well. It is in `patch_text` and nowhere else: not in
-- a log line, not in a ledger rationale and not in a refusal code, all of which carry closed
-- vocabulary codes and no content.

CREATE TABLE IF NOT EXISTS incidentcompass.remediation_diffs (
    id uuid PRIMARY KEY,
    tenant_id text NOT NULL CHECK (length(btrim(tenant_id)) > 0),
    report_id uuid NOT NULL REFERENCES incidentcompass.triage_reports (id),
    job_id uuid NOT NULL REFERENCES incidentcompass.triage_jobs (id),
    attempt integer NOT NULL CHECK (attempt > 0),
    service_name text NOT NULL CHECK (length(btrim(service_name)) BETWEEN 1 AND 128),
    source_release text NOT NULL CHECK (length(btrim(source_release)) BETWEEN 1 AND 128),

    -- Lower-hex SHA-256 content identities of the tree the diff applied to and the tree it produced.
    -- Neither is a commit id: nothing in this product reads git, so an identity says nothing about
    -- which commit, branch or upstream repository the tree came from. The base identity is the value
    -- a later application of this diff must compare its own workspace against, and refuse on a
    -- difference; without it an insert-only hunk binds the change to no tree at all.
    base_tree_identity text NOT NULL CHECK (base_tree_identity ~ '^[0-9a-f]{64}$'),
    result_tree_identity text NOT NULL CHECK (result_tree_identity ~ '^[0-9a-f]{64}$'),

    files_changed integer NOT NULL CHECK (files_changed BETWEEN 1 AND 16),

    -- The raw diff, bounded by the budget the action-approval ceiling leaves for it. The ceiling is
    -- 64 KiB of canonical JSON, not of diff text: the canonical writer escapes to six-byte \uXXXX
    -- forms, so one raw byte can become six, and reserving 1 KiB for the fields that travel beside
    -- the diff plus two bytes for its quotes leaves (65536 - 1024 - 2) / 6 = 10751 raw UTF-8 bytes.
    -- The parser refuses anything larger before a row can be written, so this bound is a backstop
    -- that agrees with it rather than a second opinion; a unit test asserts the two numbers match.
    patch_bytes integer NOT NULL CHECK (patch_bytes BETWEEN 1 AND 10751),
    patch_text text NOT NULL CHECK (octet_length(patch_text) BETWEEN 1 AND 10751),
    CHECK (octet_length(patch_text) = patch_bytes),

    -- Provenance a reviewer needs without a join to the ledger. Neither is a secret: an endpoint and
    -- a credential never leave the Infrastructure adapter that holds them.
    route_id text NOT NULL CHECK (route_id ~ '^[A-Za-z0-9_.-]{1,128}$'),
    model text NOT NULL CHECK (length(btrim(model)) BETWEEN 1 AND 200),

    -- A row exists only for a diff that applied whole. There is deliberately no way to record a
    -- refused attempt here and have a later reader mistake it for evidence.
    validation_code text NOT NULL CHECK (validation_code = 'remediation_applied'),

    -- No test is executed anywhere in this product: no process is started, and the architecture
    -- guard that fails the build when process I/O appears in the Application project is unchanged.
    -- The schema says so rather than trusting a writer to. A later release that does run a test must
    -- relax both checks in its own migration, which is the point: claiming a test ran cannot be done
    -- quietly.
    test_command_id text NULL CHECK (test_command_id IS NULL),
    test_outcome text NOT NULL CHECK (test_outcome = 'not_executed'),

    created_at_utc timestamptz NOT NULL
);

-- The read a later approval makes: the diffs prepared for one report, newest first, within a tenant.
-- Rows are never updated, so nothing here has to survive a state change.
CREATE INDEX IF NOT EXISTS ix_remediation_diffs_tenant_report
    ON incidentcompass.remediation_diffs (tenant_id, report_id, created_at_utc DESC, id DESC);
