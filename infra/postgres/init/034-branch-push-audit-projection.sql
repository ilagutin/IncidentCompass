-- Widen the compact external-action audit projection to hold what a governed branch push produced.
--
-- 025 introduced the projection for two providers that number their resources, so it constrained
-- `external_resource_id` to a positive decimal integer. A branch push produces neither a number nor a
-- name that would fit one: what it produces is a commit, and a commit is a forty-character git object
-- name. This migration therefore makes the identifier shape a function of the kind rather than one
-- rule for all kinds, and adds the kind.
--
-- Why the commit and not the branch name. The branch name is derived from the origin report, which is
-- already a column on the same row, so storing it would store a value that is recomputable from the
-- row it sits on; the commit exists only because the push happened and cannot be recovered any other
-- way. It also keeps the column a fixed-width identifier that an index can carry, instead of making
-- it the place arbitrary free-form names accumulate. Reading "which branch" back is
-- 'incidentcompass/remediation/' || replace(origin_report_id::text, '-', '').
--
-- The constraints are dropped and re-added rather than altered, which is how a CHECK is changed in
-- PostgreSQL. Every step is guarded so a re-run is a no-op, and the whole script runs under the same
-- advisory lock 025 used, because it edits the same constraints.

SELECT pg_advisory_lock(hashtext('incidentcompass:025-external-action-audit-projection')::bigint);

ALTER TABLE incidentcompass.action_approvals
    DROP CONSTRAINT IF EXISTS ck_action_approvals_external_resource_kind,
    DROP CONSTRAINT IF EXISTS ck_action_approvals_external_resource_id,
    DROP CONSTRAINT IF EXISTS ck_action_approvals_external_projection_transition,
    DROP CONSTRAINT IF EXISTS ck_action_approvals_external_projection_category;

ALTER TABLE incidentcompass.action_approvals
    ADD CONSTRAINT ck_action_approvals_external_resource_kind CHECK (
        external_resource_kind IS NULL OR
        external_resource_kind IN ('telegram_message', 'github_issue', 'git_branch'));

ALTER TABLE incidentcompass.action_approvals
    ADD CONSTRAINT ck_action_approvals_external_resource_id CHECK (
        external_resource_id IS NULL OR
        (external_resource_kind IN ('telegram_message', 'github_issue') AND
         external_resource_id ~ '^[1-9][0-9]{0,19}$') OR
        (external_resource_kind = 'git_branch' AND
         external_resource_id ~ '^[0-9a-f]{40}$'));

ALTER TABLE incidentcompass.action_approvals
    ADD CONSTRAINT ck_action_approvals_external_projection_transition CHECK (
        external_resource_kind IS NULL OR
        (external_resource_kind = 'telegram_message' AND
         external_before_state = 'not_sent' AND external_after_state = 'sent') OR
        (external_resource_kind = 'github_issue' AND
         ((external_before_state = 'absent' AND external_after_state = 'open') OR
          (external_before_state = 'open' AND external_after_state = 'comment_added'))) OR
        (external_resource_kind = 'git_branch' AND
         external_before_state = 'absent' AND external_after_state = 'created'));

-- A branch push is the only category allowed to record a branch, and it is allowed to record one
-- transition: a reference that did not exist now exists. There is deliberately no transition here
-- that could describe a reference being moved or removed, so a row claiming one cannot be written
-- even by something that bypassed the application entirely.
ALTER TABLE incidentcompass.action_approvals
    ADD CONSTRAINT ck_action_approvals_external_projection_category CHECK (
        external_resource_kind IS NULL OR
        (category = 'notification' AND external_resource_kind = 'telegram_message' AND
         external_before_state = 'not_sent' AND external_after_state = 'sent') OR
        (category = 'ticket_create' AND external_resource_kind = 'github_issue' AND
         external_before_state = 'absent' AND external_after_state = 'open') OR
        (category = 'ticket_update' AND external_resource_kind = 'github_issue' AND
         external_before_state = 'open' AND external_after_state = 'comment_added') OR
        (category = 'branch_push' AND external_resource_kind = 'git_branch' AND
         external_before_state = 'absent' AND external_after_state = 'created'));

SELECT pg_advisory_unlock(hashtext('incidentcompass:025-external-action-audit-projection')::bigint);
