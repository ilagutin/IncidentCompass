# IncidentCompass 0.3.0 - governed context and external actions

> Historical record, partly superseded (2026-09-11): 0.4.0 edited the comments of six released
> migration scripts once, so the statement below that earlier migration bytes are preserved no longer
> holds, and a database volume created under this release fails the 0.4.0 startup checksum guard and
> must be recreated. The scripts are frozen from 0.4.0 forward, and the catalog now ends at version
> 26 rather than 17. See [0.4.0](release-notes-v0.4.0.md) and [versioning](versioning.md).

IncidentCompass 0.3.0 is a reference-quality local release. It adds direct read-only investigation
context, a minimal API-key tenant boundary and backend-governed Telegram/GitHub actions without
granting the model credentials, provider targets, approval authority or direct side-effect access.

## What changed

- Incident memory applies deterministic bounded reranking with current-service and exact metadata
  signals while retaining tenant, embedding-route, dimension and active-item filters in PostgreSQL.
- The source worker can cite bounded text excerpts from explicitly configured local checkouts. The
  backend selects the service release and stack frames; the model cannot choose a root or path.
- A provider-neutral ticket-search port and fixed-authority GitHub Issues adapter return bounded,
  grounded existing-ticket evidence with sanitized unavailability limitations.
- Optional API-key authentication binds each host-managed key to one tenant, protects API-v1 data and
  OTLP routes by default, supports bounded atomic credential reload and applies a queue-free per-key
  fixed-window limit.
- One ledger-backed rule engine now governs immediate reads and backend-owned post-report proposals.
  Frozen proposal bytes, provenance and approval hashes are reviewed through tenant-scoped APIs.
- A durable evaluation queue and separate Worker dispatcher recover bounded backend work while keeping
  `action_approvals` as the only approval and external-dispatch outbox. Dispatch invokes a claimed
  action at most once and records uncertain outcomes instead of risking an automatic duplicate.
- Disabled-by-default adapters support backend-routed Telegram notifications, GitHub Issue creation
  after a repository-bound no-match and one evidence comment on an exact cited GitHub issue. GitHub
  writes require approval; general issue mutation is not exposed.
- Confirmed live Telegram/GitHub success records one compact immutable external kind/id and closed
  state transition beside the terminal action result. Raw provider responses, routes and credentials
  are not indexed or exposed by the approval API.
- An authenticated read endpoint rolls durable `ModelCall` rows into UTC-hour counts, safe token totals,
  explicit priced/unpriced counts and exact per-currency spend. Pricing is effective-dated
  operator-maintained database data.
- The deterministic Tester loads the reviewed injection fixture as a fifth scenario and performs four
  bounded reads of that exact fault ledger after report publication. Default and overridden Compose
  ports plus fresh and retained mock runs were verified without adding provider endpoint doubles.

## Also In This Release

Alongside the features above, this release includes hardening and cleanup that changes observable
behavior:

- The tool rule engine now denies an unknown rule type, a precondition rule naming no prerequisite,
  or a rate cap with no positive maximum, instead of skipping the malformed rule and falling through
  to allowed.
- Budget and governance exhaustion now dead-letter the attempt immediately with their own error codes
  instead of consuming the retry budget; provider outage still retries.
- A configurable `IncidentCompass:IngestionLimits:MaxSignalsPerExport` option (default 500) rejects an
  over-limit OTLP export whole with `413` before any signal is stored.
- Redaction patterns compile once per configuration snapshot, a regex timeout now redacts the whole
  field with a distinct marker, and the secret property-name denylist matches on name segments.
- Structured log events carry stable ids for attempt failure, retry-versus-dead-letter, model call
  outcome, budget charge/exhaustion and policy decisions, for operators building log-based alerts.

See the full [changelog](../CHANGELOG.md) for every change in this release, including internal
refactors and dependency updates not listed here.

## Breaking And Behavior Changes For v0.2.0 Upgraders

- **Pipeline behavior delegate renamed.** The dispatcher's continuation delegate
  `RequestHandlerDelegate<T>` was renamed to `PipelineContinuation<T>`, and
  `IPipelineBehavior.HandleAsync`'s `next` parameter was renamed to `continuation`. Any custom
  `IPipelineBehavior` implementation must update both names to keep building.
- **API error responses no longer echo exception messages.** `400`, `403`, `404` and `409` responses
  now carry a stable `errorCode` in the `ProblemDetails` extensions plus an authored, client-safe
  `detail`, instead of the raised exception's own message text. The documented OpenAPI response
  schema is unchanged, because the `errorCode` travels as dynamic extension data; a client that
  parsed the previous free-text `detail` for a specific substring should switch to matching on
  `errorCode`. The Worker's persisted `last_error_message` similarly now stores a classified code
  plus exception type rather than raw provider text.

## Defaults and compatibility

- The API remains under `/api/v1`. API-key authentication is disabled for the local walkthrough; a
  non-local host must inject high-entropy key digests and normal transport/deployment controls.
- Source lookup, GitHub context and all external actions remain operationally disabled until the host
  provides their exact configuration. The shipped triage config has an empty action allow-list and
  `DefaultMode` set to `disabled`.
- Notification policy may auto-approve an explicitly enabled backend-routed notification. GitHub
  create and update remain mandatory-approval writes. The model never receives either capability.
- OpenAI-compatible model and embedding routes remain the normal local runtime path. Automated tests
  and `scripts/demo.ps1 -Mock` use explicit deterministic doubles and do not call real providers.
- The migration catalog advances append-only to version 17. Mandatory-Docker coverage exercises fresh
  databases and populated upgrade paths while preserving earlier migration bytes.

## Reference limits and non-goals

- This is not a production incident platform. It does not provide enterprise identity or RBAC,
  managed key distribution, a secret store, a UI or a stable general extension framework.
- MCP/product tool catalogs, Jira, automated fix or pull-request workflows, broad evaluations and
  cross-fault incident correlation remain later work.
- Cost alerts, price administration/reload, currency conversion, quotas and a usage dashboard are not
  included. Missing, malformed or ambiguous pricing remains explicitly unpriced.
- Action dispatch provides at-most-once backend invocation, not exactly-once external delivery.
  Outcome uncertainty is durable and requires operator review rather than automatic resend.
- External-action retention, reaping and cross-system reconciliation remain deferred.
- The fifth Tester scenario is a bounded disabled-policy packaging observation, not a universal
  no-action proof. Configured-policy, requested-only approval and zero-provider-call behavior are
  exercised separately with in-process recording handlers.

## Verification

The release gate ran `dotnet restore IncidentCompass.slnx --locked-mode` clean, then
`dotnet build IncidentCompass.slnx --no-restore` with zero warnings and zero errors. Formatting
(`dotnet format --verify-no-changes`), the code-organization gate and the package-vulnerability gate
all reported no findings.

`dotnet test --solution IncidentCompass.slnx` with `INCIDENTCOMPASS_REQUIRE_DOCKER_TESTS=true`
recorded 863 total tests: 860 passed, 0 failed, 3 skipped, with Docker-backed integration coverage
included. The three skips are expected and unchanged in nature from prior releases: a symlink-escape
test that needs privileges this platform does not grant, an opt-in real-model smoke test gated behind
`INCIDENTCOMPASS_LLM_SMOKE_ENABLED`, and an explicit OpenAPI-baseline regeneration fact that only
`scripts/update-openapi-baseline.ps1` runs.
