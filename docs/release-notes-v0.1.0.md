# IncidentCompass 0.1.0 - first public reference release

> Historical record, partly superseded (2026-09-11): the demo runs five scenarios since 0.3.0, which
> also added optional API-key authentication, two more read tools and governed external actions; and
> 0.4.0 removed the prompt-logging setting named below in favour of a guarantee, because no code path
> writes that material anywhere. The local smoke rates at the end describe the 0.1.0 build and no
> longer describe this one; 0.4.0 publishes a measured run instead. See
> [0.4.0](release-notes-v0.4.0.md) for current behaviour.

IncidentCompass is a governed incident-triage agent backend, built as a **reference-quality
implementation** - not a production system and not a stable framework. An incident signal from any
supported source is normalized deterministically, a governed AI investigation runs under configured
permissions, rules, and budgets, and the result is an evidence-backed triage report where every
citation resolves to a stored artifact of that run.

## What is in 0.1.0

- **Deterministic intake** - `POST /api/v1/incidents` with per-source normalizers (tester, OTel-shaped,
  user, manual), best-effort secret redaction, deterministic fingerprinting with strong/weak grouping
  semantics, silence-window suppression, recurrence linking, and content-addressed triage config
  snapshots (`config_hash`) so every job is reproducible against the exact configuration that ran it.
- **Governed investigation loop** - a Worker claims jobs (bounded by `MaxConcurrentJobs`), rehydrates
  the job's config from its snapshot, and runs an orchestrator whose only tools are `delegate` and
  `publish_report`. Worker roles get exactly the tools their config grants; every tool call passes
  `ToolProposed → PolicyDecision → execute → ToolResult` through a rule engine (`rate_cap`,
  `precondition`, `requires_approval` fail-closed, `grounding` seam) evaluated over the durable ledger,
  scoped to the current attempt.
- **Durable audit ledger** - every delegation, policy decision, tool result, model call, and budget
  event is written as its own commit as it occurs, with DB-assigned ordering; the final report,
  evidence, fault status, job completion, and the `ReportPublished` event commit in one fenced,
  atomic transaction that a stale attempt cannot overwrite.
- **Memory worker (RAG)** - an internal `memory_search` tool over pgvector with exact
  tenant/provider/model/dimension filters, honest no-match output, and attempt-level `RetrievedItem`
  artifacts that reports can cite.
- **Grounded reports** - `publish_report` is backend-validated: status/classification pairing,
  citable-kind grounding by exact artifact id (a worker's own conclusion is not citable), quotes kept
  only when they are verbatim substrings of the cited artifact, `is_mass_issue` and evidence kinds
  stamped by the backend, never taken from the model. Read surface:
  `GET /api/v1/faults/{id}`, `GET /api/v1/faults/{id}/ledger`, `GET /api/v1/triage-reports/{id}`.
- **One-command demo** - `pwsh scripts/demo.ps1` builds non-root images, starts Postgres + API +
  Worker via compose, seeds memory, and drives four deterministic scenarios (runbook-backed timeout,
  honest unknown, mass issue, noise) with report and ledger URLs. Requires only Docker; no API keys.

## Defaults and honest framing

- **Mock model and embedding providers are the default.** The gated demo and test suite prove the
  **governance, grounding, and packaging rails around a scripted trajectory** - they do not
  demonstrate model autonomy. Optional real-LLM smoke runs against a local OpenAI-compatible endpoint
  are opt-in, non-gated, and recorded in the Real-Model Smoke History table in `docs/trade-offs.md`.
- **"Grounded" means every citation resolves to a stored artifact of the run** - it does not mean the
  backend verified the reasoning. Controls, not correctness.
- Endpoints are **unauthenticated demo scope** (local/trusted network only) and the deployment is
  **single-tenant**; `tenant_id` is stamped by the backend and never read from the envelope.
- Prompt/body logging is disabled by default; redaction is best-effort pattern matching, not a
  guarantee.

## Verification scope

- CI gate: locked-mode restore, build, format, code-organization and package-vulnerability gates, and
  the full test suite including Docker-backed integration tests (Testcontainers + pgvector).
- Every commit in this repository's history passes that gate independently from a clean checkout.
- The demo is verified cold (fresh volume, image build from a clean checkout) and warm (restart over
  a seeded volume).

## Known non-goals and limitations

See `docs/trade-offs.md` for the maintained list, including: no external actions (Jira/Slack/PR) in
this release; approval is a fail-closed seam without resume; retries re-investigate (no mid-run
resume); leases are not renewed mid-attempt; single-instance PostgreSQL with no HA/backup story;
some components ship dormant, reserved for roadmap items. The improvement backlog and phase plans
are maintained outside the repository until the corresponding features land.
