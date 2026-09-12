-- Widen the compact external-action audit projection to hold what a governed pull request produced.
--
-- 025 introduced the projection for two providers that number their resources; 034 made the identifier
-- shape a function of the kind so that a branch push could record a commit. This migration adds the one
-- kind the last link of the publication chain produces: a pull request, named by the number the provider
-- gave it. That shape is the positive decimal integer the numbering kinds already use, so this adds a
-- kind and a category and introduces no new notion of identity.
--
-- Why a kind of its own rather than a second use of 'github_issue'. A provider may number issues and
-- pull requests in one sequence, but they are different resources with different lifecycles, and an
-- auditor filtering the column for issues should not have to know that a pull request is one. Keeping
-- them apart also keeps the transition rules apart, which is what the last constraint below relies on.
--
-- What is deliberately absent. There is exactly one transition for a pull request: it did not exist, and
-- now it is open. There is no 'merged', no 'closed' and no 'auto_merge_enabled', so a row claiming this
-- product merged anything cannot be written even by something that bypassed the application entirely.
-- That is the database's own statement of the same promise the port and the request shapes make.
--
-- The constraints are dropped and re-added rather than altered, which is how a CHECK is changed in
-- PostgreSQL. Every step is guarded so a re-run is a no-op, and the whole script runs under the same
-- advisory lock 025 and 034 used, because it edits the same constraints.

SELECT pg_advisory_lock(hashtext('incidentcompass:025-external-action-audit-projection')::bigint);

ALTER TABLE incidentcompass.action_approvals
    DROP CONSTRAINT IF EXISTS ck_action_approvals_external_resource_kind,
    DROP CONSTRAINT IF EXISTS ck_action_approvals_external_resource_id,
    DROP CONSTRAINT IF EXISTS ck_action_approvals_external_projection_transition,
    DROP CONSTRAINT IF EXISTS ck_action_approvals_external_projection_category;

ALTER TABLE incidentcompass.action_approvals
    ADD CONSTRAINT ck_action_approvals_external_resource_kind CHECK (
        external_resource_kind IS NULL OR
        external_resource_kind IN (
            'telegram_message', 'github_issue', 'git_branch', 'github_pull_request'));

ALTER TABLE incidentcompass.action_approvals
    ADD CONSTRAINT ck_action_approvals_external_resource_id CHECK (
        external_resource_id IS NULL OR
        (external_resource_kind IN ('telegram_message', 'github_issue', 'github_pull_request') AND
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
         external_before_state = 'absent' AND external_after_state = 'created') OR
        (external_resource_kind = 'github_pull_request' AND
         external_before_state = 'absent' AND external_after_state = 'open'));

-- A pull-request creation is the only category allowed to record a pull request, and it is allowed to
-- record one transition. The ticket-update category is unchanged and still admits only a comment being
-- added to an issue, which is what the governed backlink writes: the backlink is a comment on an issue
-- that mentions a pull request, not a write to the pull request itself.
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
         external_before_state = 'absent' AND external_after_state = 'created') OR
        (category = 'pr_create' AND external_resource_kind = 'github_pull_request' AND
         external_before_state = 'absent' AND external_after_state = 'open'));

SELECT pg_advisory_unlock(hashtext('incidentcompass:025-external-action-audit-projection')::bigint);
