# Integration configuration

Configure optional integrations after completing the [Quickstart](quickstart.md). External actions ship disabled.

## Source context

Local source lookup is disabled operationally until a host configures an exact
`IncidentCompass:SourceContext:Roots` entry containing `ServiceName`, `Release` and an absolute
`RootPath`. Optional absolute `BuildPathPrefixes` translate known build-agent paths only on path
segment boundaries. The selected release still comes exclusively from the snapshotted
`CurrentReleases` entry for the fault service. Removing the `source` role or its `source_lookup`
grant from triage configuration removes the tool from the model surface.

## GitHub Issues

GitHub Issues search is disabled operationally until the Worker host receives
`IncidentCompass__Tickets__GitHub__Owner`, `IncidentCompass__Tickets__GitHub__Repository` and the
secret `IncidentCompass__Tickets__GitHub__Token`. The token is a host secret and does not enter the
public triage configuration or its snapshots. Requests always target `https://api.github.com` with
redirects disabled. Removing the `tickets` role or its `ticket_search` grant removes the tool from
the model surface. `ticket_create` is a separate, never-model-callable post-report action. Enabling it
requires an exact `Actions.AllowedTools` grant and non-disabled mode while retaining the same Worker
repository binding. It is eligible only after exactly one current-attempt, repository-bound
`ticket_search` no-match. `ticket_update` is a distinct post-report action that can only add one
bounded evidence comment to exactly one cited `ExistingTicket` in that configured repository; it
cannot change title, state, labels or assignees. Every ticket write remains `requested` until an
authenticated operator approves the frozen hashes. Bounded target and marker lookup may precede at
most one issue or comment POST; uncertainty after a POST begins is visible and never automatically
resent.

## Telegram

Telegram notification is also disabled by default. Enabling it requires one exact
`telegram_notify` entry in `Actions.AllowedTools`, one ordered `Actions.NotificationRoutes` entry
for that tool, and Worker-only host values for `IncidentCompass__Telegram__Enabled`,
`IncidentCompass__Telegram__RouteId`, `IncidentCompass__Telegram__ChatId` and the secret
`IncidentCompass__Telegram__BotToken`. The public route can match normalized service and environment
plus a closed severity subset, but it contains no chat id, token, endpoint or message template. At
most 32 routes are allowed and the first match wins. Requests use the fixed Telegram API authority
with redirects and `parse_mode` disabled. Dry-run and pending-approval evaluation make no HTTP call;
only a later live approved dispatch can invoke the adapter.

## Authentication and approvals

API-key authentication is disabled by default for the local walkthrough. A non-local API host must
enable `IncidentCompass__ApiKeyAuth__Enabled`, set startup-static `PermitLimit` and `WindowSeconds`,
and inject one or more credential entries containing only a stable key id, tenant id and SHA-256
hex digest. Clients send the corresponding 32-128 character base64url secret in exactly one
`X-IncidentCompass-Key` header. See [Security model](security-model.md) for reload, tenant and
rate-limit behavior. Do not place a raw key in tracked configuration.

Action approval routes are always stricter than the local walkthrough. The entire
`/api/v1/action-approvals` group returns `403` when API-key authentication is disabled. When it is
enabled, any valid host-issued key is the minimal action operator for its mapped tenant until a later
RBAC slice. Demo headers and the config-default tenant never grant action review authority. Optional
`externalResourceKind` and `externalResourceId` list filters must be supplied together; supported
kinds are `telegram_message` and `github_issue`, and ids are positive decimal provider identities.

## Cost rollups

The cost-rollup route uses the same authenticated key-to-tenant binding. It requires `fromUtc` and
`toUtc` with UTC offsets, treats the start as inclusive and the end as exclusive, and rejects empty,
reversed or greater-than-31-day windows. Provider, model, logical route and tenant identifiers are not
returned. Calls with missing or ambiguous effective pricing remain explicitly unpriced instead of
being reported as zero-cost usage. See [Cost tracking](cost-tracking.md).

## GitHub code publication

`branch_push` publishes an approved, executed `code_write` as one new branch at one new commit. It
reuses the GitHub Issues binding above - the same owner, the same repository and the same
`IncidentCompass__Tickets__GitHub__Token` - and adds one setting,
`IncidentCompass__Publication__GitHub__BaseBranch`, which has no default. With it unset, code
publication is not configured on the host and every call refuses. Enabling the action also requires an
exact `Actions.AllowedTools` grant and a non-disabled mode, and the Worker refuses to start when the
action is enabled without a repository, a credential and a base branch.

Setting it widens what the shared token needs: creating a branch requires write access to repository
contents, which filing issues does not. `docs/trade-offs.md` explains why one credential is preferred
to two.

The adapter talks to `https://api.github.com` with redirects disabled, exactly as the issue adapters
do. It reads a reference, a commit and one recursive tree listing, creates the blobs, tree and commit
its request describes - all content-addressed, so creating one twice changes nothing - and then makes
exactly one `POST /git/refs`. There is no reference update, no reference delete and no merge call
anywhere in the adapter, and the port above it has no operation that could ask for one. The base
branch is read and never written; the branch that is created is derived from the origin report as
`incidentcompass/remediation/<report>` and is never taken from a payload or a model. A reference
create whose outcome is unknown is durable, is never retried automatically, and is settled by one read
of that derived reference.
