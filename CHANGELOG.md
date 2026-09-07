# Changelog

## Unreleased

- No unreleased changes.

## 0.3.0 - 2026-09-06

- Added deterministic bounded memory reranking that preserves tenant, embedding-route and active-item
  isolation while improving current-service retrieval.
- Added governed read-only source lookup over explicitly configured local checkouts and GitHub Issues
  search through provider-neutral Application ports with fixed host-owned authorities.
- Added optional API-key authentication with server-side key-to-tenant binding, deny-by-default API
  coverage, bounded credential reload and per-key fixed-window rate limiting.
- Consolidated immediate reads and post-report action proposals on one ledger-backed rule engine.
- Added durable frozen action proposals, tenant-scoped approval review, bounded post-report evaluation
  and at-most-once Worker dispatch with visible outcome uncertainty.
- Added disabled-by-default Telegram notification plus GitHub Issue create and bounded evidence-comment
  adapters. Notification routing is backend-owned; GitHub writes require approval.
- Added compact immutable external-resource projections for confirmed Telegram and GitHub success,
  exposed through tenant-scoped approval list/get filtering without raw provider responses.
- Added authenticated UTC-hour model-cost rollups from durable `ModelCall` rows with effective-dated
  operator-maintained pricing, exact per-currency totals and explicit unpriced accounting.
- Added the reviewed injection fixture as a fifth deterministic Tester scenario with a bounded exact
  fault-ledger action gate, plus verified default/overridden Compose ports and fresh/retained mock runs.
- Updated pinned dependencies and release workflow action revisions.
- Added `.editorconfig` with enforced code style in the build, enabled `GenerateDocumentationFile` so
  unused-using violations (IDE0005) are caught at build time, and pinned `samples/**/*.json` to LF
  line endings because those fixtures are hash-pinned by tests.
- Centralized package versions in `Directory.Packages.props`, turned on `TreatWarningsAsErrors` and
  `AnalysisLevel=latest-recommended`, pinned the SDK floor in `global.json` and removed
  `coverlet.collector`. Breaking change: the pipeline delegate `RequestHandlerDelegate<T>` was renamed
  to `PipelineContinuation<T>`, and `IPipelineBehavior.HandleAsync`'s `next` parameter was renamed
  `continuation`; a custom pipeline behavior must update to match.
- Removed eight development worklog files from `docs/`, kept the real-model smoke measurements as a
  table in `docs/trade-offs.md`, and moved ingestion payload limits from the README into
  `docs/quickstart.md`.
- Changed the tool rule engine to deny an unknown rule type, a precondition rule naming no
  prerequisite, and a rate cap with no positive maximum, instead of skipping them and falling through
  to allowed.
- Added structured log events with stable ids for attempt failure, retry versus dead-letter, model
  call outcome, budget charge and exhaustion, and tool and action policy decisions; the `ModelCall`
  ledger payload gained a named type with an unchanged serialized shape.
- Changed budget and governance exhaustion to dead-letter the attempt immediately with their own error
  codes instead of consuming the retry budget; provider outage still retries.
- Behavior change: 400, 403, 404 and 409 API responses now carry a stable `errorCode` in the
  ProblemDetails extensions and an authored `detail`, instead of echoing the raised exception's
  message. The documented OpenAPI schema is unchanged because extension data is dynamic. The Worker's
  `last_error_message` now stores a classified code plus exception type rather than raw provider text.
- Added the `IncidentCompass:IngestionLimits:MaxSignalsPerExport` option (default 500, range
  1-10000); an OTLP export carrying more records than the limit is rejected whole with 413 before
  anything is stored.
- Changed redaction to compile patterns once per configuration snapshot, redact the whole field with
  a distinct marker on a regex timeout instead of leaving it partially escaped, and match the secret
  property-name denylist on name segments so `x-api-key`, `Set-Cookie`, `jwt` and `*_password_*` forms
  are caught.
- Made fault injection a test-only seam, extended the code-organization gate to cover test projects,
  collapsed pass-through pipeline wrappers, reworked the investigation orchestrator loop around
  explicit turn outcomes, removed dead Worker lease-renewal signals and unobserved delays, and
  deduplicated provider selection and configuration leaks in composition.
- Added a shared repository-root test helper with deterministic lease timing, and rewrote the
  security posture, defaults and observability documentation to match current behavior.

## 0.2.0 - 2026-07-14

- Added a versioned triage-config JSON Schema and `config validate` command that reuses startup validation without starting a host or persisting a snapshot.
- Added configurable attribute and pattern redaction plus host-secret HMAC pseudonymization for configured user identifiers; missing salt fails safe to redaction.
- Replaced content-addressed sample seeding with concurrent-safe file sync: stable source identity, frontmatter metadata, re-embedding on edits and deactivation on removal.
- Changed local/demo defaults to OpenAI-compatible model and embedding providers. Mock providers now require explicit test/mock configuration such as `compose.mock.yml` or test host overrides.
- Added environment substitution for route-level model names in `config/incidentcompass.config.json` through `INCIDENTCOMPASS_LLM_MODEL` and `INCIDENTCOMPASS_EMBEDDINGS_MODEL`.
- Replaced the opt-in `compose.real-llm.yml` with `compose.mock.yml`: `scripts/demo.ps1` now runs real providers by default and takes `-Mock` to select the deterministic mock override.
- Added native OTLP/HTTP trace and log intake with a shipped OpenTelemetry SDK Tester and stock Collector route into the normal intake pipeline; duplicate deliveries are idempotent.
- Hardened memory sync with owner-safe generations, divergent and partial scan protection, bounded runtime resync and Worker-persisted metadata-only health.
- Added versioned grouping, suppression, transaction-safe recurrence escalation and exactly-once recurrence re-triage.
- Made published reports immutable with supersession history, latest/history reads, compact keyset-paginated report lists and server-owned tenant scoping across fault, ledger and report reads.
- Renewed Worker leases during processing, added durable provider-unavailable delay and bounded local claim backpressure with automatic recovery.
- Added metadata-only runtime telemetry for triage, governed tools, migrations and memory sync with closed outcome tags and exporter-failure isolation.
- Hardened release publication around exact event-SHA evidence, a Docker-backed full test gate, one tag publisher and idempotent main-only recovery dispatch.

## 0.1.1 - 2026-07-10

- Added `IC_API_PORT` and `IC_POSTGRES_PORT` overrides for Compose host mappings while keeping
  internal service ports stable.
- Limited retry investigation context to job-level artifacts and artifacts from the current attempt.
- Redacted normalized typed string fields before fingerprinting, persistence and model prompts.
- Serialized signal attachment with fault terminalization and re-resolved grouping when the
  candidate fault closed concurrently.
- Increased the Development worker lease above the shipped investigation wall-clock budget and
  added a regression check for that cross-configuration invariant.
- Normalized active PostgreSQL adapter failures into an Application-level persistence exception
  without exposing provider messages or intercepting cancellation and invariant failures.

## 0.1.0 - 2026-07-04

First public reference release of IncidentCompass, a governed incident-triage agent backend:
deterministic backend intake, a governed orchestrator/worker investigation loop over a durable
audit ledger, grounded triage reports, and a one-command local demo. Reference quality, not a
production system.

This release includes:

- Repository bootstrap from the upstream starter-kit reference: layered .NET structure, model/embedding gateway, generic dispatch/health/security/user scaffolding, and PostgreSQL infrastructure.
- Intake: POST /api/v1/incidents, GET /api/v1/faults/{id}, source allow-list validation, tester/OTel/user/manual normalizers, redaction, deterministic fingerprinting, strong/weak grouping semantics, silence-window suppression, recurrence linking, triage config snapshots, pending triage jobs, and job-level intake artifacts.
- Governed investigation loop: Worker claim loop with MaxConcurrentJobs, config-snapshot rehydration, a backend-owned orchestrator surface limited to delegate and minimal publish_report, sequential worker execution, worker output artifacts, durable own-commit triage ledger events, and minimal report persistence that closes the job/fault.
- Governance rails: bounded orchestrator and worker reprompts, worker-tool rule evaluation over the durable ledger, role-scoped backend tool grants, audit-visible ToolProposed/PolicyDecision/ToolResult events, per-attempt budget accounting with ModelCall and first-class BudgetEvent delta columns, context-window guardrails, and full load validation for routes, roles, tools, rules, schemas and budget knobs.
- Memory worker: PostgreSQL memory_items/memory_chunks, deterministic sample seeding, pinned mock embedding model, governed memory_search execution for the memory role only, exact tenant/provider/model/dimension retrieval filters, attempt-level RetrievedItem artifacts, honest no-match output and live rate_cap coverage against the real tool.
- Grounded reports: backend-validated publish_report payloads, exact citable artifact references, quote substring validation, backend-derived is_mass_issue and evidence kind, same-transaction report/evidence/job/fault/ReportPublished commit, prior-report readback and GET /api/v1/triage-reports/{id}.
- Demo packaging: GET /api/v1/faults/{id}/ledger, the HTTP-only IncidentCompass.Tester console/container, API and Worker Dockerfiles that copy config/ and samples/ with explicit in-image path overrides, non-root container users, compose health/restart wiring, the demo compose profile, scripts/demo.ps1, opt-in compose.real-llm.yml, one-shot output examples in shipped instructions, and the honest local-demo documentation.
- Pre-publication hardening: removed the unused AI request logging stack and request-log table, removed obsolete model-gateway request-policy scaffolding, removed unreachable API error mappings, made the standalone governed tool-audit and pricing components explicitly dormant for later approval and cost-accounting capabilities, and added a PostgreSQL readiness health check.
- PostgreSQL schemas in infra/postgres/init/007-intake.sql, infra/postgres/init/008-triage-ledger.sql, infra/postgres/init/009-triage-reports-minimal.sql and infra/postgres/init/010-memory.sql: intake rows, artifacts, DB-ordered ledger events, first-class ToolResult status, first-class BudgetEvent deltas, grounded triage reports/evidence, and memory rows/chunks with write-time embedding dimension checks.

Notable defaults and invariants:

- Deterministic mock model and embedding providers remain the default for automated tests and the one-command local demo.
- The Tester runs four deterministic scenarios: runbook-backed checkout timeout, unknown null-reference, provider-unavailable mass issue and validation/noise.
- The gated demo proves packaging plus backend governance/grounding rails around a scripted mock trajectory; optional real-LLM runs are non-gated and do not change the reference release bar.
- Strong fingerprints require both a real non-unknown service name and structured errorType; user/manual reports with only a service name remain weak.
- Full rendered prompt/body logging remains disabled by default, and redaction preserves null optional signal text fields.
- The Worker wires the governed investigation loop and the shipped config has one live worker tool: memory_search, granted only to the memory role.
- Non-gated local smoke improved from the baseline (qwen2.5-14b-instruct: 0/N useful completions) to 3/3 publish_report completions with MaxReprompts: 2; memory-enabled investigation reached memory-backed publish_report 5/5 in the recorded local smoke. Real-LLM use remains optional; the deterministic release gate remains the mock-backed demo plus automated tests.

Chat, RAG document ingestion, evaluations, usage dashboards and MCP host/client product surfaces remain out of scope for IncidentCompass.

Not a production system.
