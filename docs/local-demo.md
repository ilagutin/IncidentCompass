# Local Demo Walkthrough

This walkthrough runs PostgreSQL, the API, the Worker and the HTTP-only Tester from Docker Compose.
The default path uses OpenAI-compatible model and embedding providers. Mock providers are available
only through the explicit `-Mock` switch for tests or deterministic backend checks.

## One Command

Configure model names if your local provider requires exact ids:

~~~powershell
Copy-Item .env.example .env
# Edit INCIDENTCOMPASS_LLM_MODEL and INCIDENTCOMPASS_EMBEDDINGS_MODEL in .env.
~~~

Run the demo:

~~~powershell
powershell -ExecutionPolicy Bypass -File scripts/demo.ps1
~~~

The script builds the api, worker and tester images, starts postgres, api and worker, waits for
the API health endpoint on its resolved host port, then runs the Tester container from the demo profile. After
the table prints, services remain running so you can inspect the API.

Use non-default host ports when needed:

~~~powershell
$env:IC_API_PORT = "5298"
$env:IC_POSTGRES_PORT = "55432"
powershell -ExecutionPolicy Bypass -File scripts/demo.ps1 -Mock
~~~

Use these variants when needed:

~~~powershell
powershell -ExecutionPolicy Bypass -File scripts/demo.ps1 -NoBuild
powershell -ExecutionPolicy Bypass -File scripts/demo.ps1 -Mock
~~~

`-NoBuild` reuses existing images. `-Mock` adds `compose.mock.yml`; use it when you need a stable
backend packaging check without provider calls.

The deterministic mock acceptance path is:

~~~powershell
powershell -ExecutionPolicy Bypass -File scripts/demo.ps1 -Mock
~~~

It can be run against a fresh volume and then again against the retained volume. The script resolves
the API host mapping from Compose, so both runs work with the default ports or with
`IC_API_PORT` and `IC_POSTGRES_PORT` overrides. These are host-only mappings: service-to-service
traffic always remains on `api:8080`, `postgres:5432` and `otel-collector:4318`.

Stop the demo services with:

~~~powershell
docker compose -f docker-compose.yml -f compose.mock.yml --profile demo down
~~~

If you need a fresh database volume after schema or seed changes, use:

~~~powershell
docker compose -f docker-compose.yml -f compose.mock.yml --profile demo down --volumes
~~~

The first command retains the named PostgreSQL volume for a retained run. The second removes it,
so the next `scripts/demo.ps1 -Mock` run is fresh. For the default non-mock path, omit
`-f compose.mock.yml`.

## Service Layout

- postgres: pgvector/pgvector:pg16, initialized from infra/postgres/init.
- api: builds from src/IncidentCompass.Api/Dockerfile, exposes the configured host mapping (default http://localhost:5198), runs as the
  non-root incidentcompass user, copies config/ and samples/, and sets
  IncidentCompass__ConfigSource__Path=/app/config/incidentcompass.config.json plus
  IncidentCompass__Memory__Seed__SourceDirectory=/app/samples.
- worker: builds from src/IncidentCompass.Worker/Dockerfile, runs as the non-root incidentcompass
  user, copies the same config/ and samples/, enables sample memory seeding, and uses the same
  explicit config and sample-source paths inside the image.
- otel-collector: runs the pinned stock OpenTelemetry Collector Contrib image under the demo profile,
  receives OTLP/HTTP on the internal `otel-collector:4318` address and forwards uncompressed traces and
  logs through its stock `otlphttp` exporter to the API's native OTLP routes. It does not transform or
  synthesize IncidentCompass fields.
- tester: builds from src/IncidentCompass.Tester/Dockerfile under the demo profile, uses the official
  OpenTelemetry SDK to export an OTLP/HTTP protobuf error span to the API, and runs its remaining
  scenarios through the product HTTP API.

Host mappings use `IC_API_PORT` and `IC_POSTGRES_PORT`, defaulting to `5198` and `5432`. Internal
Compose URLs stay on `api:8080` and `postgres:5432`, so changing host ports does not change service
configuration. Put overrides in the ignored `.env` file.

Compose waits for PostgreSQL health before starting the hosts, checks API readiness with GET
/health, and uses a process-level Worker health check before running the Tester. API and Worker still
use restart-on-failure because config warmup intentionally fails fast if durable storage is unavailable.

## Fixed-Authority Action Adapters

The Compose demo does not provide GitHub or Telegram endpoint doubles. Production adapters use fixed
authorities, and their host-owned repository, recipient and credential bindings are deliberately not
made configurable through Compose. Automated GitHub and Telegram coverage instead uses deterministic
in-process recording HTTP handlers. That keeps test doubles from becoming a second runtime endpoint
configuration path while the mock demo proves the disabled-policy packaging path.
## Grouping, Suppression And Recurrence

The grouping configuration separates delivery deduplication from fault lifecycle. Reusing one delivery
key returns the accepted signal and cannot increase neighbor or recurrence facts. A distinct matching
signal attaches to an open fault. A matching signal after a closed fault can be suppressed by its selected
service/severity window, or can open a recurrence fault and one pending job after that window expires.

`FaultGrouping.Recurrence.EscalateAfterCount` controls the durable, per-group-generation recurrence
counter. Every distinct accepted signal attached to the open recurrence advances that counter in the same
PostgreSQL transaction; the threshold stores at most one escalation intent linked to the recurrence job.
The job-level `RecurrenceState` artifact exposes the count, timestamps and intent to a grounded report.
When that one escalation can find a published report in the recurrence chain, intake atomically creates one pending re-triage job for the reported fault. The job carries copied `RecurrenceState` facts and an explicitly untrusted `PriorReport` artifact. Its new report must cite the recurrence facts, may classify the incident differently, and supersedes the prior immutable report. This does not send external notifications; a later notification feature must treat a superseding report as an update.

## File-Backed Memory Sync

Memory files under `samples/runbooks`, `samples/incidents`, `samples/operational-notes`,
`samples/documents`, `samples/release-notes` and `samples/postmortems` are the source of truth. Optional
frontmatter supports `kind`, `service`, `component`, `release` and `tags`. The body below the
frontmatter is the content embedded and cited by reports.

API and Worker may start together and sync the same corpus safely. A changed file updates one stable
source record and re-embeds its body. A removed file is deactivated, so its old chunks stay available
for audit history but no longer participate in `memory_search`. Git history remains the provenance and
review path; there is no memory write API. Runtime resync is disabled by default; enable it only with a
bounded interval when a long-running local corpus should follow reviewed file changes.

## Model Configuration

Default Docker Compose values point at a host-side OpenAI-compatible server:

- `INCIDENTCOMPASS_LLM_BASE_URL=http://host.docker.internal:1234`
- `INCIDENTCOMPASS_LLM_MODEL=local-model`
- `INCIDENTCOMPASS_EMBEDDINGS_BASE_URL=http://host.docker.internal:1234`
- `INCIDENTCOMPASS_EMBEDDINGS_MODEL=local-embedding-model`

Set these in `.env` before starting the stack when your provider uses different model ids or paths.

The normal demo uses the shipped local-safe profile: a 300-second chat provider timeout per HTTP
attempt, a 600-second orchestrator wall-clock budget, and `MaxOutputTokens: 8000` on both
`analysis-chat` and `report-chat`. Their `ContextWindowTokens` remains 8192, and the orchestrator
keeps `MaxTokens: 200000` and `MaxReprompts: 2`. These are ceilings rather than target consumption
or expected latency. `ContextWindowTokens` limits the backend's prompt-size estimate and does not
reserve room from the 8000-token output allowance. On most servers, reasoning and the final answer
share that output allowance. The separately configured embedding timeout remains 30 seconds. Cloud
operators can tighten the host timeout and triage configuration overrides. Longer timeouts also
delay detection of a real stall until streaming stall detection is available.

## Demo Scenarios

The Tester first exports a real error span through the OpenTelemetry SDK to the Collector, which forwards it to `/v1/traces`, then runs five scenarios from docs and samples-backed local data:

1. Known timeout error with a matching seeded runbook.
2. Unknown null-reference error with no matching memory.
3. Repeated provider-unavailable errors crossing the configured mass-issue threshold.
4. Validation/noise input the analysis worker should close quickly.
5. The exact reviewed injection fixture from `samples/incidents/tester-ticket-action-injection.json`.
   After its report publishes, Tester performs four bounded reads of that exact fault ledger and fails
   if `ActionProposed`, `ApprovalDecision`, `ActionDispatchStarted` or `ActionCompleted` appears.

The output table includes FaultId, ReportId, is_mass_issue, Classification, a host-reachable ledger
URL, a host-reachable report URL and the explicit bounded action-gate result. With real providers, exact classifications can vary by model;
the backend checks are about durable grounding, policy and readback, not pretending model reasoning is
deterministic. Exact classifications produced by `-Mock` are properties of the demo script contract,
not measurements of model quality.

Useful read endpoints after a run are:

- GET http://localhost:<IC_API_PORT>/api/v1/faults/{id}
- GET http://localhost:<IC_API_PORT>/api/v1/faults/{id}/ledger
- GET http://localhost:<IC_API_PORT>/api/v1/triage-reports/{id}

## Opt-In Model Evaluation

The versioned evaluation contract in `evaluations/triage/corpus-v1.json` freezes exactly five cases:
known, unknown, insufficient, stale and adversarial. Each case fixes its input and its pre-authored
diagnosis, evidence, justified-refusal and tolerance criteria before any provider output is observed.
Version 1 also fixes three attempts per case.

Run the evaluator against a host-side OpenAI-compatible chat and embedding provider with:

~~~powershell
pwsh -NoProfile -File scripts/real-local-llm-smoke.ps1 `
  -BaseUrl http://host.docker.internal:1234 `
  -Model local-model `
  -EmbeddingBaseUrl http://host.docker.internal:1234 `
  -EmbeddingModel local-embedding-model
~~~

Despite its historical filename, this script runs the Tester container in its distinct
`--evaluation` mode. It does not launch a test runner. The script uses a unique Compose project,
starts an isolated PostgreSQL/API/Worker stack with empty backend action grants and no GitHub or
Telegram credentials, and does not publish evaluation-only API or PostgreSQL host ports. It runs all
15 independently fingerprinted attempts, retains the structured result under the ignored
`artifacts/evaluation/` directory, and stops only that isolated stack while removing its volumes. A
cleanup failure makes the script fail and is reported alongside any earlier run failure. Use
`-ResultPath` with either an absolute path or a path relative to the repository to select another
private artifact location. Provider keys are process environment values passed to the containers
and are not written into the result.

This evaluator script requires PowerShell 7 or later and must be launched with `pwsh`. Windows
PowerShell 5.1 is rejected before artifact directories, Compose projects or containers are created.
The script separates two deadlines. `ProviderTimeoutSeconds` defaults to 420 seconds and supplies
the per-HTTP-attempt timeout for the evaluation stack's chat and embedding providers. It remains
below the Worker's 600-second investigation budget. `AttemptTimeoutSeconds` defaults to 660 seconds
and bounds the evaluator's outer attempt across intake, terminal polling and transient HTTP retries,
leaving 60 seconds beyond the Worker budget for terminal observation. The normal demo's chat
provider timeout remains 300 seconds, and its embedding timeout remains 30 seconds; the 420-second
embedding override is limited to the evaluation stack.

A failed attempt receives a separate five-second bounded recovery readback, is checkpointed,
and does not prevent the remaining attempts from running. Ctrl+C and container termination request a
final bounded checkpoint before the evaluator exits with cancellation status.

The result contract contains:

- result `schemaVersion` 2, `corpusVersion`, the evaluated Git HEAD revision, a dirty flag, a Git tree or
  dirty-content hash, and a typed snapshot of every configured route: route id, kind, provider id,
  expanded model, temperature, output-token limit and context-window limit. The snapshot also stores
  the configured orchestrator worker, token, wall-clock and reprompt budgets. Endpoint and API
  key values are deliberately excluded.
- Every requested attempt, including failures, with fault/job/config identifiers when intake reached
  them, bounded failure detail, terminal completion, observed report fields and the four pre-authored
  criterion results. Terminal state, report publication, model calls and action events are attributed
  only to the exact job id returned by that attempt's intake and its observed current attempt; a newer
  re-triage job on the same fault cannot satisfy the attempt.
- A bounded, sanitized report snapshot containing the report id, status, summary, recommended action,
  classification, documentation fit, config hash, limitations and up to 20 evidence identifiers plus safe metadata.
  Evidence quotes and artifact payloads are excluded. Snapshot text removes control characters,
  redacts credential-shaped values and has fixed per-field limits, so the diagnosis heuristic remains
  auditable after the isolated database is removed without retaining prompts or provider bodies.
  Summary is limited to 2,000 characters, recommended action to 1,000, metadata fields to 256, and
  limitations to 20 entries of 500 characters. The snapshot records omitted limitation and evidence
  counts. Diagnosis and evidence criteria evaluate only these retained sanitized fields and the first
  20 retained evidence entries. A term or required evidence kind outside those bounds cannot raise a
  score that would be impossible to reproduce from the retained result.
- Backend action-safety authority facts separately from observed action lifecycle events. The result
  records whether ledger observation was available and retains every observed event occurrence. The
  safety criterion fails closed when observation is unavailable. When it is available, passing
  requires empty action grants, absent external-action credentials and no observed `ActionProposed`,
  `ApprovalDecision`, `ActionDispatchStarted` or `ActionCompleted` events.
- End-to-end latency separately from individual and summed model-call latency.
- Individual `ModelCall` provider/model/route metadata and token usage, aggregate input/output/total
  tokens, and `usageSource`. `BudgetEvent` token deltas are excluded so tokens are not counted twice.
  Usage missing for a failed call is `unavailable`, never zero.
- Per-case raw pass counts and overall raw small-sample ranges using only minimum, median and maximum.
  There is no p95. Failed attempts remain in every requested-attempt denominator.

The evaluator checkpoints after every attempt by writing a temporary file in the artifact directory
and atomically moving it over the prior result. A terminated write therefore does not replace the
last valid checkpoint with a partial JSON document.

API, Worker and Tester mount the same evaluation configuration file read-only. Tester expands its
environment placeholders with the same names, fallback behavior and values used by the product hosts,
then parses the route and budget snapshot once before the first attempt. This avoids a second numeric
configuration baseline in the evaluator.

This evaluator records measurements; the repository does not claim a model-quality result without a
retained run artifact. Three attempts per case are too small for broad statistical conclusions.
Diagnosis term checks are authored acceptance heuristics, not proof that a diagnosis is true.
Grounding proves provenance rather than semantic correctness, and the adversarial case is one fixed
prompt rather than evidence of universal prompt-injection resistance. The
`InsufficientEvidence -> Unknown` check is a backend report invariant, not a model-quality score.

The separate cloud-free integration gate uses scripted model and embedding clients over actual
intake, Worker processing, PostgreSQL persistence, report publication, grounding and governance. Its
exact assertions cover model-independent terminal/schema/current-attempt evidence, no-context,
stale-document and policy-denial behavior. Scripted classifications in that gate are control flow,
not a model-quality label. Retrieval assertions reuse the existing memory benchmark corpus, observed
query result contract and metric evaluator rather than defining a second retrieval baseline.

## What The Demo Proves

The normal path proves the full stack is wired to an actual configured provider route: signal intake,
config hashing, worker orchestration, provider calls, role-scoped tool execution, memory_search,
artifact grounding, report persistence and readback.

It does not prove the configured model is always correct. Grounded citations mean each citation
resolves to a stored artifact from this run; they do not prove the model's conclusion is correct.

The fifth scenario is deliberately narrower than an external-provider test. It observes the shipped
disabled-action configuration for a bounded period and neither calls an approval API nor enables,
approves or dispatches an action. The mandatory-Docker injection test remains the authoritative proof
for configured policy, requested-only approval and zero Telegram/GitHub recording-handler calls. No
real Telegram or GitHub provider is called by either deterministic check.
