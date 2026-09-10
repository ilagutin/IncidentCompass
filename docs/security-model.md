# Security Model

The core principle is simple: the LLM is not a security boundary.

The backend decides what data and tools are available. The model may summarize, reason and propose actions, but it must not enforce authorization or receive privileged credentials.

## Demo Auth

IncidentCompass uses:

- `IUserContext` in the application layer;
- demo/fake authentication for local development;
- headers, seeded users or configuration as demo identity sources.

Real auth providers such as Entra ID or ASP.NET Identity are future adapters, not requirements for the local sample path.

The API registers the demo header-based `IUserContext` only for `Development` by default (`appsettings.Development.json` sets `IncidentCompass:DemoAuth:Enabled` to `true`; the base `appsettings.json` leaves it `false`). Development requests may omit headers and use the configured local `demo-user` defaults for the quickstart. Startup fails unless the API composition root registers a real foreground `IUserContext` adapter before the app starts: `ApiUserContextSetup.AddApiUserContext` always registers `ApiUserContextStartupFilter` (`src/IncidentCompass.Api/Security/ApiUserContextStartupFilter.cs`), and that filter throws unless a non-background `IUserContext` was resolved. In practice this only bites in Production and other environments where neither API-key auth nor demo auth ends up enabled, because Development and explicit demo opt-in register `DemoHeaderUserContext` first. The Infrastructure project registers `IBackgroundUserContext` for Worker/system jobs, not a foreground API `IUserContext`, so the background identity cannot satisfy the API auth requirement by DI ordering. Non-production demo environments can explicitly opt in to demo headers; in that opt-in mode, the configured default user, tenant, roles and groups are ignored, the request must include an explicit `X-Demo-User-Id` to be treated as authenticated, and anonymous requests receive no default claims. Worker hosts explicitly map the background context for job processing and do not use HTTP demo headers.

Demo headers such as `X-Demo-User-Id`, `X-Demo-Tenant-Id` and `X-Demo-Roles` are caller-controlled sample inputs. They are useful for local walkthroughs, but they are not authentication and must not be trusted in deployed environments.

## API-key boundary

The API host supports a minimal shared-key boundary through host-only
`IncidentCompass:ApiKeyAuth` settings. It ships disabled: `src/IncidentCompass.Api/appsettings.json`
sets `IncidentCompass:ApiKeyAuth:Enabled` to `false`, with an empty `Credentials` list, and no
environment-specific appsettings file turns it on. A deployment that wants the shared-key boundary
must explicitly set `Enabled` to `true` and supply credentials through host configuration. When `Enabled` is true, a fallback authorization policy
protects all current and future endpoints unless they are explicitly anonymous. The complete
anonymous allowlist is `/health`, `/api/v1/health`, `/api/v1/health/memory-sync` and the
Development-only OpenAPI document. Manual intake, incident-data reads, `users/me` and native OTLP
trace/log ingestion all use the same boundary.

Action approval routes use a dedicated operator policy. The complete
`/api/v1/action-approvals` group returns `403` before user-context resolution, dispatch or repository
access whenever API-key authentication is disabled. When it is enabled, any valid host-issued key is
the minimal action operator for exactly its mapped tenant until RBAC is added. Demo identity, demo
headers and the config-default tenant never grant approval authority. Foreign action ids return `404`.

Clients send exactly one `X-IncidentCompass-Key` value. It must be 32-128 ASCII base64url
characters with no padding, commas or whitespace. The host stores only its SHA-256 hex digest and
compares the digest in fixed time. A credential also has a non-secret stable key id and exactly one
tenant id. Successful authentication supplies both `IUserContext` and `IIncidentTenantContext`
from that server-owned mapping, so request bodies, OTLP attributes and demo headers cannot choose
the tenant. Missing, malformed and invalid credentials return `401` before endpoint binding or
Application dispatch.

`Enabled`, `PermitLimit` and `WindowSeconds` are startup-static. Protected requests share a
queue-free fixed-window limiter partitioned only by authenticated key id; anonymous health and
OpenAPI requests are not limited. A valid configuration reload atomically rotates the immutable
credential map. An invalid reload, or an attempted live change to a startup-static field, installs
a deny-all map until a fully valid configuration with the original static fields arrives or the
process restarts. This avoids retaining a potentially revoked credential during a broken reload.

Rejects increment `incidentcompass.api.authentication.rejections` with only the bounded outcome
`missing`, `malformed` or `invalid`. Raw keys, configured digests and request bodies are excluded
from auth logs, metrics, errors, the triage ledger and configuration snapshots. Host transport may
return `431` before application code for a header block above its own size limit.

Credentials are injected through host configuration or environment variables. They are not public
triage configuration and no raw key belongs in tracked files. For example, credential fields use
`IncidentCompass__ApiKeyAuth__Credentials__0__KeyId`, `__TenantId` and `__Sha256Digest` suffixes.
This is minimal authentication, not RBAC, key distribution, a secret store, OAuth or a production
identity platform.

## Incident Data Tenant Scope

`IIncidentTenantContext` is separate from `IUserContext`. With API-key authentication enabled it
reads the tenant mapped to the authenticated key. In explicitly auth-disabled local/demo mode it
reads `Ingestion.DefaultTenant` from the server-loaded triage configuration. `X-Demo-Tenant-Id`,
incident-envelope fields, OTLP resource attributes and other sender-controlled data never select
the incident-data tenant.

Fault, ledger, report and action approval read/decision paths resolve this server-owned scope before querying. An object
outside the scope is indistinguishable from a missing object and returns `404`; compact report
lists only return scoped rows. API-key authentication changes only the API composition adapter,
not the intake or read use cases. Worker/system jobs continue to use their background identity and
the server-loaded job/configuration tenant context.

## Local Compose Credentials

`docker-compose.yml` sets local-only PostgreSQL demo defaults through `${VAR:-default}` fallbacks:
`POSTGRES_USER` and `POSTGRES_PASSWORD` default to `incidentcompass` and
`incidentcompass_dev_password`; the `postgres` healthcheck and the `api` and `worker`
`ConnectionStrings__IncidentCompass` values reuse the same fallbacks. These values exist only so the
one-command demo runs without an operator supplying anything; overriding them from an ignored `.env`
file or shell variables replaces them without editing the compose file (see `docs/local-demo.md` and
`docs/quickstart.md`).

The repository ships four compose files, and only the first carries demo credentials:

- `docker-compose.yml` is the local demo stack. It is the only file that supplies default database
  credentials, and it must not be deployed as-is.
- `compose.mock.yml` is a deterministic-provider overlay for that demo stack. It replaces the model
  and embedding providers and nothing else.
- `compose.evaluation.yml` is a local evaluation overlay for that demo stack. It closes the published
  ports, mounts the evaluation triage configuration read-only and clears the Telegram and GitHub
  credentials for the run.
- `compose.production.yml` is the bounded single-host overlay described in
  `docs/single-host-production.md`. It carries no demo credentials of its own: the database,
  provider, API-key and source values it sets are required variables, so Compose refuses to start
  when one is missing and the base file's demo defaults cannot take effect. Demo auth is forced off,
  and the API and PostgreSQL ports bind to loopback. It is a reference deployment for one trusted
  machine and one trusted operator, not a hardened multi-tenant production template.

A real deployment must still supply its own configuration and secrets management. Neither
`docker-compose.yml` nor its default credentials may be deployed as-is.

## Logging

- Full rendered prompt logging is disabled by default.
- Metadata logging is allowed: request ID, user ID, model, tokens, cost, status.
- If full prompt logging is ever enabled, it must require opt-in, redaction, encryption, retention policy and restricted access.
- Tool execution is controlled by backend policy. The model may propose tool calls, but it cannot execute tools directly and never receives infrastructure credentials.
- The API error boundary never echoes an exception's own message to a client. `NotFoundException`,
  `ConflictException`, `ForbiddenRequestException` and `ValidationException` map to a `ProblemDetails`
  response carrying a stable `errorCode` and an authored, client-safe `detail`; the original exception
  is logged server-side only, tagged with the request's correlation id (`HttpContext.TraceIdentifier`)
  and the same error code (see `docs/observability.md`).

## Cost read boundary

The authenticated model-cost endpoint obtains its tenant only from the host-bound `IUserContext` and
accepts no tenant selector. Its bounded window query joins ledger rows through tenant-owned faults.
The response exposes UTC buckets, counts, token totals and per-currency spend only, not tenant, fault,
job, provider, model or logical route identifiers. Malformed ModelCall history and missing or
overlapping prices fail closed to unpriced and are never echoed to responses or logs. Pricing remains
operator-maintained database configuration; this read surface grants no price, alert or quota authority.

## Local source read boundary

The source worker never receives a filesystem root, release selector or arbitrary read argument.
Host options allowlist exact service/release roots and optional build-path prefixes; the job's
snapshotted `CurrentReleases` entry is the only release selector. Candidate paths are canonicalized
and revalidated below the selected root before opening, reparse/symlink traversal is rejected, and
only configured text extensions within byte, frame, candidate and excerpt limits are read. Source
bodies and absolute host paths are not logged or persisted. Durable artifacts contain only a
repository-relative path, bounded excerpt, line range, release and `heuristic` mapping label.

## Intake Redaction And Pseudonymization

Built-in secret patterns remain active for every signal. The triage config can add attribute-key
redaction and bounded .NET regular-expression replacements before persistence and before model calls.
These rules are defense in depth, not a guarantee that every possible secret or PII shape is known.

A built-in property-name denylist redacts values whose JSON property name looks like a secret holder,
independently of the configured attribute keys. The rule an operator can predict: a property name is
lowercased and split into segments on every non-alphanumeric character and on camel-case and
letter/digit boundaries, and the value is redacted when any run of consecutive segments joins to one
of `accesskey`, `apikey`, `authorization`, `clientsecret`, `connectionstring`, `cookie`, `jwt`,
`passwd`, `password`, `privatekey`, `secret` or `token`; the value is also redacted when the whole
name, with separators removed, equals the whole-name-only term `session`. So `x-api-key`, `Cookie`,
`Set-Cookie`, `Authorization-Bearer`, `user_password_hash`, `db_password_2`, `access_token` and a
property named exactly `session` are redacted, while `session_id`, `sessionCount`,
`session_start_time`, `keyword`, `key_count` and `totalTokens` are not. Broad words are whole-name
terms precisely so that ordinary incident context is not destroyed; the trade-off is that a name such
as `token_count` is redacted and a plural such as `cookies` is not. Like the configured rules, this
matcher is best effort (see `docs/trade-offs.md`).

Configured patterns are compiled once per configuration snapshot and each match runs under a 200 ms
timeout. A pattern that exceeds it fails closed: the entire field is replaced with the distinct marker
`[REDACTED:PATTERN_TIMEOUT]`, never with the partially processed intermediate value, so a hostile
input cannot pass through a redaction rule that did not finish and cannot abort intake either. The
timeout is logged once per pattern per configuration snapshot with the pattern name and the field
path only. The field value is exactly the text redaction failed to clean, so it is never logged.

Configured user-identifier attributes are replaced before redaction with an HMAC-SHA256 pseudonym.
The salt comes only from host secrets or `IncidentCompass__Pseudonymization__Salt`; it is not stored in
the triage config, config snapshot, artifact or ledger. If the salt is absent, identifiers fail safe to
`[REDACTED]`, so distinct-user continuity is unavailable but raw identifiers are not stored. Rotating
the salt changes every pseudonym and breaks counts across the rotation boundary.

## Tools

Tool execution must go through backend policy. Risky tools require approval or must be rejected. The LLM must not receive infrastructure credentials. The investigation loop gives the orchestrator only backend-owned `delegate` and `publish_report` actions; `delegate.role` is generated from configuration and validated again before execution. Worker-tool proposals are recorded as `ToolProposed`, checked against role grants and ledger-backed rules, recorded as `PolicyDecision`, and only allowed backend calls execute. Unknown, unregistered, ungranted and invalid worker tool calls fail closed with audit-visible decisions. The shared rule engine also fails closed on the rules themselves rather than skipping what it cannot evaluate: a rule whose type it does not recognise is denied (`unknown_rule_type`), a `precondition` rule that names no prerequisite tool is denied (`precondition_missing_prerequisite`), and a `rate_cap` rule without a positive maximum is denied (`rate_cap_missing_max`). Every denial names its cause the same way: a stable reason code, optionally followed by `: ` and a human detail, with the code always the leading token of the recorded reason. The post-report denial vocabulary is derived from that code rather than from the wording after it, so rephrasing a denial message cannot change which denial an operator or a read model sees. The same engine decides immediate reads and post-report action proposals, so both paths deny identically; load-time configuration validation rejects the same malformed rules earlier, but the engine does not depend on having been called with validated configuration. Immediate reads and external actions are separate backend capabilities: role grants accept only immediate tools, `Actions.AllowedTools` accepts only exact registered external tools, and external actions are never advertised to the investigation model. Startup and `config validate` reject capability mismatches, wildcard or read-tool approval targets and action metadata that disagrees with registration. Report publication is also fail-closed: the model may name evidence references, but the backend accepts only citable artifacts from the same job/current attempt, never `WorkerOutput`, derives evidence kind and `is_mass_issue` itself, and marks prior reports as untrusted hypotheses in the artifact payload.

The durable post-report evaluation queue is backend-owned. Report publication selects only startup-
registered workflows whose exact tool id, workflow version, category and logical target match the
external-action registry, then commits their intents atomically with the report. Canonical workflow
input contains only the origin report id, tool id, version and optional bounded route id. It cannot
carry report or evidence text, model output, provider input, credentials or an adapter-selected
destination. Invalid input, missing exact catalog membership and exhausted attempts fail closed.
Evaluation may invoke only the existing governed proposal use case; it never receives adapter authority.

Post-report proposal creation is backend-owned and starts only from a current immutable published
report and its exact configuration snapshot. The registered tool selects category, logical target
and binding; arguments cannot replace them. Global disabled mode denies, dry-run cannot be loosened,
and every non-notification write requires approval. The shared rule engine reads external preconditions
and accepted-proposal caps only through the fault-first proposal transaction after its current-origin
and job recheck. Registered and configured tool ids use one bounded case-sensitive safe grammar. A
safe-origin denial records only a closed reason code; input rejected before same-tenant origin resolution writes nothing. Proposal creation never
invokes the external tool adapter.

Approval decisions submit the exact observed payload and approval hashes. A stale lifecycle state,
expiry, hash mismatch or superseded origin conflicts without approval. The public review surface
contains the frozen safe tuple, canonical payload and backend-derived provenance only. It excludes
adapter binding inputs, credentials, raw routes, prompts, transcripts and evidence bodies. No current
API endpoint edits payloads, dispatches an action or retries an outcome.

Approved action dispatch is a separate Worker path. The Worker rechecks current policy only as a
tightening guard, recomputes the registered adapter-binding fingerprint and passes the immutable
stored bytes plus the action id only to the exact registered external-action capability. Frozen or
newly tightened dry-run performs no adapter call; disabled, approval-tightened, unregistered and
binding-drift cases fail closed with bounded durable evidence. Application composition registers
only the non-secret Telegram, ticket-create and ticket-update descriptors for public configuration
validation. Their workflows, adapters, host bindings and credentials are registered only in the
Worker host.
API and shared test host configuration do not require or receive provider credentials.

Confirmed live action results may expose one compact external-resource projection through approval
list/get responses. The projection uses a closed resource kind, a positive decimal provider identity
and a closed bounded before/after marker. It contains no token, recipient, repository authority,
request route, provider body, prompt or report text. Exact list lookup requires kind and id together,
uses a tenant-leading partial index and always applies the authenticated server-owned tenant. A
foreign resource identity therefore returns the same empty list shape as an absent identity.

The durable claim is the at-most-once boundary. Once it records an owner, random fence and database
deadline, no automatic path may call that adapter again. Exceptions, timeout, cancellation and crash
recovery become `dispatch_outcome_unknown`; deadline recovery fences a late completion. This prefers a
visible uncertain result over a duplicate external side effect. Adapters own credentials and endpoint
authority, must observe cancellation and must normalize provider exceptions before returning across
the Application port.

The GitHub Issues token is bound only from Worker host configuration, normally the
`IncidentCompass__Tickets__GitHub__Token` environment variable. It is absent from public triage
configuration, config snapshots, tool definitions, prompts, artifacts and report payloads. The
repository is also host-owned; model arguments, incident fields and tenants cannot select another
repository or API authority. The adapter does not log authorization headers, response bodies or
issue bodies. Authentication, rate-limit, timeout and malformed-response failures are reduced to a
closed sanitized code before they reach durable tool outcomes or report limitations. Caller/job
cancellation on read-only search propagates instead of being misreported as a connector timeout.

Ticket create additionally requires exactly one current-attempt durable no-match bound to that same
repository. The check and current binding comparison occur inside the fault-locked proposal
transaction. The model cannot call the create tool or provide repository, owner, authority, token,
marker or category. Every create remains requested until the tenant operator submits exact frozen
hashes. Before the single possible POST, bounded local history and GitHub marker lookup fail closed;
an earlier uncertain marker is read-only and can never authorize another POST. Once the POST starts,
transport or response ambiguity becomes `dispatch_outcome_unknown` and is not retried.

Ticket update is a separate mandatory-approval capability and is limited to one backend-built evidence
comment on exactly one cited `ExistingTicket`. The provider-neutral Application resolver exposes only
report and ticket identity; Infrastructure binds the evidence to the configured repository and
rechecks it inside the fault-locked proposal transaction. Model output, API input and proposal
arguments cannot choose a repository or replace the cited issue. The adapter validates the exact
issue with bounded target and comment-history reads before one possible POST. Missing, foreign,
multiple, malformed or ambiguous evidence and any preflight failure authorize no write. An existing
marker suppresses another comment; uncertainty after POST begins becomes `dispatch_outcome_unknown`
and is never automatically resent.

Telegram routing separates public policy from secret host authority. The snapshotted configuration
contains at most 32 ordered route ids with optional normalized service/environment selectors and a
closed severity subset. First match wins; there is no fanout and no recipient, endpoint, token or
message template in the route. The Worker binds the selected route id to one fixed chat id and bot
token through `IncidentCompass__Telegram__RouteId`, `__ChatId` and `__BotToken`. The adapter accepts
only backend-generated report and route ids, builds the bounded message itself, always targets
`https://api.telegram.org`, follows no redirects and sends no `parse_mode`. Provider bodies, request
paths and tokens are never returned or durably recorded. Once a mutating send starts, exceptions and
cancellation are outcome-unknown and are not retried.
