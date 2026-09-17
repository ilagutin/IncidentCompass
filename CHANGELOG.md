# Changelog

## Unreleased

- No unreleased changes.

## 0.5.0 - 2026-09-16

Memory embeddings run inside the Worker by default, seed documents are chunked by section, and a slow
model is bounded by per-phase provider limits and a long attempt ceiling instead of a short total
deadline, with chat completions streamed by default. Every tool call has an execution limit, and an
investigation that stops making progress gets bounded recovery and then an honest backend-authored
report.

### Added

- An in-process local embedding model, `LocalOnnx`, as the shipped default for memory embeddings. The
  Worker runs `intfloat/multilingual-e5-small` (int8 ONNX file and SentencePiece tokenizer) with ONNX
  Runtime, pinned to one Hugging Face revision and to each file's SHA-256. On start it installs or
  verifies the model in the `embedding-models` volume at `/app/models`: an installed manifest is
  re-verified and kept, files placed offline are verified and never fetched over, and missing files
  are downloaded over HTTPS within a size limit and renamed into place only when the digest matches.
  A failed install does not stop the Worker; it records a named code and every local embedding call is
  refused until a later start installs the model. `THIRD-PARTY-NOTICES.md` lists the model and the
  runtime packages, and a test fails on a shipped license other than MIT or Apache-2.0.
- A locally embedded corpus records its model as `<model id>@sha256:<first 16 hex of the model file
  digest>`, so a model file replaced under the same id is a detected embedding route change. The
  Worker reports `memory_embedding_model_mismatch` when the installed id is not the route's model and
  `memory_embedding_model_unavailable` when no usable model is installed; both publish nothing and
  keep the previous corpus current.
- `memory model status` and `memory model install` Worker commands. Status prints the installed
  model's id, revision, license, encoded identity and digests beside the configured route and the
  corpus model, and exits 1 on any mismatch. Install places the configured model beside the current
  one within the install timeout, keeps the replaced manifest as `manifest.previous.json` for
  rollback and never deletes an installed file.
- Structural chunking of memory seed documents. Each file is split on ATX headings and then capped by
  tokens on whole lines with a small overlap (`IncidentCompass:Memory:Seed:Chunking`, defaults
  `MaxTokens` 448, `OverlapTokens` 48, `MinTokens` 32). Token counts come from the installed model's
  tokenizer for the local adapter and from a character estimate for the OpenAI-compatible and mock
  adapters. Every chunk starts with its heading path, which is stored in `memory_chunks.heading_path`
  and returned as `headingPath` by `memory_search`. A heading path or line that cannot fit is refused,
  naming the seed file.
- Each corpus generation records its chunk policy in `memory_corpus_generations.chunk_policy`. A
  changed policy publishes nothing, keeps the previous corpus current and reports
  `memory_chunk_policy_changed` until `memory rebuild`. Migration `036-memory-chunk-structure.sql` adds
  both nullable columns and rewrites no existing row.
- Per-phase limits for one OpenAI-compatible chat HTTP attempt under
  `IncidentCompass:ModelGateway:OpenAiCompatible`: `ConnectTimeoutSeconds` (30),
  `FirstOutputTimeoutSeconds` (600) and `StreamInactivityTimeoutSeconds` (600). Each retry gets fresh
  limits, and a stall is recorded as `provider_connect_timeout`, `provider_first_output_timeout` or
  `provider_stream_inactivity_timeout`.
- `Orchestrator.Budget.MaxAttemptDurationSeconds`, an optional ceiling on one investigation attempt.
  Absent means 14400 seconds, `0` disables it.
- Streamed chat completions. The chat client requests `stream: true` with usage by default and
  assembles content, tool calls, finish reason and usage from server-sent events into the same
  completion a JSON answer produces. `IncidentCompass:ModelGateway:OpenAiCompatible:Streaming=false`
  restores the previous request, and a provider that ignores `stream` and answers with JSON still
  works.
- A configurable execution limit per tool, `Tools.<id>.TimeoutSeconds` (1 to 3600). An immediate
  worker tool defaults to 120 seconds and an external action to the Worker's
  `ActionDispatch:AdapterTimeoutSeconds`. A worker tool bounded by its own limit is recorded as a
  failed tool result with `tool_execution_timeout` and the worker continues; the attempt ceiling ends
  the attempt with the existing wall-clock code; shutdown propagates without a ledger write; any other
  exception is recorded as `tool_execution_failed` and rethrown. The executor stops waiting when a
  bound fires even for a tool that ignores cancellation.
- Repetition detection. A worker tool call with the same tool and canonical arguments, or a delegate
  with the same role and task, that has already been repeated
  `Orchestrator.Budget.MaxEquivalentCalls` times in a row with an unchanged result is refused before
  it runs with `repeated_call_without_new_evidence`, without failing the attempt (with the default 2,
  the first call and two identical repeats run and the fourth call is refused). The result identity
  ignores the fresh artifact ids a call writes, and a result that changed resets the count. Once a
  call is refused it is not run again in that attempt, so data behind that exact call is not re-read
  later. A worker that proposes two refused calls in a row is stopped without an answer and the
  orchestrator receives `worker_stopped_repeating`.
- Progress detection, bounded recovery and honest termination. When consecutive orchestrator turns add
  no new evidence and no changed candidate classification past
  `Orchestrator.Budget.MaxTurnsWithoutProgress` (default 4, 2 to 32), the backend makes up to
  `Orchestrator.Budget.MaxRecoveries` (default 1, 0 to 3) tool-less recovery calls per attempt, one per
  detected stall and only while another no-progress window still fits, with call kind `recovery` and a
  summary of counts and names only, and hands its bounded answer to the orchestrator as a suggestion.
  With no recovery left, no room for another window, the turn or worker budget spent inside a detected
  stall, or the token budget or context window leaving no room for the recovery call, the backend
  publishes its own `InsufficientEvidence` report with fixed sentences, its `ReportPublished` ledger
  rationale opens with `backend_authored: `, and the job succeeds. A provider outage during the
  recovery call is handed to the job runner as for any call. `Orchestrator.RecoveryInstructions`
  optionally replaces the recovery instructions, shipped as `config/instructions/recovery.md`.
- An opt-in retrieval benchmark for the local embedding models, run only when
  `INCIDENTCOMPASS_EMBEDDING_BENCHMARK` is set. It measures `multilingual-e5-small` and
  `multilingual-e5-base` on the retrieval corpus with English queries and Polish and Russian
  renderings that reuse the same relevance labels.

### Changed

- The Worker is the only process composed with an embedding client. The memory seed and resync pass,
  `memory status`, `memory rebuild` and the `memory model` commands run on the Worker; the API keeps
  the corpus status and health readers, which need no model. An architecture test fails if API
  source names the embedding adapters or the corpus command.
- The shipped configuration routes `memory-embed` to a `local-embed` provider of kind `LocalOnnx`, and
  both Compose files select the `LocalOnnx` host provider unless `INCIDENTCOMPASS_EMBEDDINGS_PROVIDER`
  says otherwise. The OpenAI-compatible embedding adapter stays an operator choice, which production
  preflight checks only when it is selected. The evaluation stack keeps the OpenAI-compatible
  embedding provider.
- Every embedding request states whether its input is a query or a passage. The local adapter applies
  the model's prefixes; the OpenAI-compatible and mock adapters ignore it.
- The shipped attempt ceiling is 14400 seconds, where the shipped `MaxWallClockSeconds` was 600, so a
  slow model that keeps producing is no longer failed at a short total deadline. A call canceled by
  the ceiling still dead-letters.
- Each model call asks the provider for at most the smaller of the route's `MaxOutputTokens` and the
  tokens left in the attempt budget after the estimated prompt. A call with nothing left is refused
  before dispatch, and a fallback skipped for that reason is recorded as a
  `max_tokens_reached_before_call` budget event.
- A chat stream fails closed. An `error` event, a stream that ends without a finish reason and a
  response that breaks off after it started are `provider_dispatch_outcome_unknown` and are never
  replayed; only the provider error code of an error event is kept, never its message. A malformed or
  inconsistent tool-call fragment is `invalid_response`. A chat response is capped at 32 MiB and a
  single event line at 4 MiB characters, both ending the call as `provider_response_too_large`.
  Providers that stream tool-call fragments without `index` need `Streaming=false`;
  `docs/model-gateway.md` lists the known incompatibilities.
- A completion cut off at the output ceiling is refused on both the JSON and the streamed path. Any
  answer whose first choice reports `finish_reason: length` is `provider_output_limit_reached`, with or
  without partial content and with or without tool calls; the partial text is discarded and never
  reaches a caller, and a tool call the ceiling cut through is reported under that code rather than as
  an invalid tool call. Previously such an answer was returned as a normal completion whenever it
  carried any text or tool call, so a cut-off report or remediation diff could be used as if it were
  whole. The job dead-letters without a retry, as an output limit already did; the fix is a higher route
  `MaxOutputTokens` or a smaller task.
- `publish_report` accepts exactly one argument shape per call (`report_json` alone, `report` alone or
  a bare report) instead of silently taking the first wrapper it found. `summary` (4000 characters),
  `recommendedNextAction` (2000), `limitations` (20 items of 1000), `evidence` (50 items) and `quote`
  (1000) are refused rather than truncated past their limit, with a reprompt that names it, and the
  tool description states the limits.
- Production Compose gives the API and the Worker the same memory seed tenant and owner, and the API
  image no longer carries the sample runbooks or sets a seed source directory.
- The Dependabot lock-file workflow runs `dotnet restore` in a job with no permissions and no secret,
  and a separate push job that runs no `dotnet` command validates the uploaded lock files against an
  allow-list before pushing. The push job uses a short-lived GitHub App installation token when
  `DEPENDABOT_LOCKFILE_APP_ID` and `DEPENDABOT_LOCKFILE_APP_PRIVATE_KEY` are set.
- The Tester reads either attempt ceiling key, and its evaluation result adds
  `maxAttemptDurationSeconds` with `attemptDurationSetting` naming its source.
- An approved action stopped after its claim but before its adapter was invoked, shutdown included,
  closes as `dispatch_not_invoked` instead of `dispatch_outcome_unknown`. The adapter deadline and the
  claim deadline both use the tool's execution limit.
- New log events 2701 and 2801 for deprecated configuration keys, 3213 for a skipped fallback whose
  budget event could not be recorded, 3305 to 3309 and 3521 for tool execution limits, and 3404 to
  3411 for repetition, progress, recovery and termination. Every no-progress intervention writes a
  `BudgetEvent` with the `no_progress:` prefix, and tool-call telemetry gains the outcome `refused`.

### Deprecated

- `Orchestrator.Budget.MaxWallClockSeconds` is replaced by `MaxAttemptDurationSeconds`. A
  configuration file or stored snapshot that sets only the old key keeps its value; loading such a
  configuration file logs a warning, while rehydrating a stored snapshot does not. Setting both is a
  load error for a configuration file and for a rehydrated snapshot alike.
- Chat `IncidentCompass:ModelGateway:OpenAiCompatible:TimeoutSeconds` is replaced by
  `FirstOutputTimeoutSeconds`. When only the old key is set its value is the first-output limit, with
  a warning at host start; setting both fails validation at start. Both checks run only when the chat
  provider is OpenAI-compatible. The embedding section's `TimeoutSeconds` is not deprecated.
- The `DEPENDABOT_LOCKFILE_TOKEN` personal access token still works as a fallback push credential
  when the GitHub App secrets are not set, and the job summary says when it was used.

### Fixed

- A `memory_search` call refused because the installed local model does not match the route or no
  usable model is installed is a configuration failure, not a provider outage. The attempt stores
  `memory_embedding_model_mismatch` or `memory_embedding_model_unavailable`, spends the ordinary
  attempt budget and dead-letters when it runs out, and never pauses job claims. A model that is
  installed but fails to load still counts as an outage.
- The production API read memory health under a seed scope the Worker never wrote, so the
  memory-sync and memory-corpus health endpoints reported a corpus nothing maintained.
- A role output schema that typed a secret-named property as an object, number or boolean passed
  validation and then failed to parse on every attempt, because redaction replaces that value with a
  string. The configuration now refuses such a schema at load and in `config validate`, naming the
  role, the schema path and the rule that matched, and a kind mismatch the load check cannot see ends
  the delegate as a non-retryable invalid worker output.
- A remediation pass whose model call was answered but whose ledger accounting failed was
  dead-lettered as `remediation_model_call_failed`. It now writes the owed row and dead-letters as
  `remediation_answer_unrecorded`, with the answer discarded and no diff kept, and is not retried.
- A chunk overlap no longer spends part of its budget on the passage prefix and sequence markers, and
  a document made only of headings becomes one chunk carrying its heading path.

### Security

- No token is reachable from the Dependabot workflow job that runs `dotnet restore`, so a process
  started during restore cannot affect the push through `$GITHUB_ENV`, `$GITHUB_PATH` or
  `.git/config`, and the pushed files are limited to validated tracked `packages.lock.json` paths.
- The local embedding model is fetched only from HTTPS locations and accepted only when both files
  match their pinned SHA-256; a mismatch is refused and never repaired.

## 0.4.1 - 2026-09-12

A patch release. Every fix in it makes something 0.4.0 already claims in public true, and none of it
adds a capability.

### Fixed

- The API and the Worker retry their first PostgreSQL connection within a budget expressed in
  configuration, at `IncidentCompass:Postgres:StartupRetry`, instead of treating a database that is
  not listening yet as fatal and relying on the container restart policy to recover. Only a socket
  failure, an `IOException` such as a connection dropped mid-handshake, a timeout and SQLSTATE
  `57P03` are retried; an unresolvable host, a wrong credential and a missing database still fail on
  the first attempt. The shipped Compose files already gate both services on the database health
  check; this covers a host started outside that gate and the window after it passes. The retry is
  latched to the first success, so a database that goes away in steady state still surfaces at once,
  and an exhausted budget fails the host loudly with the normalized persistence error.
- A model client that raises anything other than a normalized provider exception or a caller-driven
  cancellation is now recorded rather than lost. The governed caller converts the breach into a
  durable `ModelCall` row under the error code `provider_contract_violation`, so the exception every
  caller depends on is exhaustive instead of something an adapter could escape unaccounted. The row
  names no answering adapter and carries no usage, so the call counts and is never priced.
- A worker tool can no longer put free text into `triage_artifacts.domain_ref`. A domain reference is
  built through a bounded type that refuses control, format, unassigned, private-use and ill-formed
  code points, whitespace other than the plain space, and the separator, and it now passes the same
  redactor the payload passes. Where the reference is built from connector text, one that cannot be
  expressed is refused rather than thrown: a source match is dropped with the new
  `source_reference_rejected` limitation, and a release id or role key that could never be expressed
  is rejected when the configuration loads.
- The `ToolResult` artifact's redaction marker now answers for the whole tool call rather than for
  its output alone, so the two citable kinds a tool call produces cannot disagree about whether
  anything was withheld. A report's withholding sentence no longer depends on which of the two the
  model cites.
- The cost rollup says what it covers. An embedding call writes no `ModelCall` row, so it is absent
  from every count, token total and spend total the rollup produces rather than merely unpriced; the
  response carries that statement and `docs/cost-tracking.md` explains it, both worded by call kind
  and adapter so they stay true if an embedding is served locally.

### Changed

- `GET /api/v1/observability/cost-rollups` gains one additive response property,
  `spendCoverageStatement`. No existing property changed.
- `docs/security-model.md` now states where the tool redaction boundary ends: the action-result
  writer records a dispatched action's own result outside it, and rows written before the boundary
  existed are not rewritten. Both say which rows are affected and why.

## 0.4.0 - 2026-09-11

### Added

- A governed remediation chain from a published report to a pull request and a ticket backlink. A
  `remediation_diff` pass asks a model for a unified diff against a named base, validates it and
  applies it inside a disposable workspace copy, and records it. `remediation_apply` freezes that diff
  into a `code_write` approval, `branch_push` publishes an approved, executed code write as one new
  branch at one new commit, `pr_create` opens one pull request from that branch into the configured
  base, and `ticket_backlink` adds one comment to the issue the report cited. Every external write is
  a separate approval a person grants by echoing the payload and approval digests; `code_write`,
  `branch_push` and `pr_create` are not auto-approvable categories. Nothing merges: no port method,
  request field, adapter call or audit transition can express a merge, an automatic merge or a force
  update. All five tools ship with `"Mode": "disabled"` and an empty `Actions.AllowedTools`, so
  turning any of them on is a deliberate two-place edit.
- **No test command is executed anywhere in this release.** A prepared diff is validated by parsing
  and by applying it to a disposable copy, never by running anything. The diff row carries
  `test_outcome = 'not_executed'` and a null `test_command_id` under database CHECK constraints, the
  frozen approval payload repeats it three times inside the hashed bytes and ends with a sentence
  saying that approving it approves an untested change, and the reviewer's summary begins with
  `UNTESTED CHANGE`. A release that runs a test has to change the migration and the payload contract
  deliberately. The decision was taken because a fixed test command would execute model-written code
  inside a process holding every credential the deployment has.
- A content identity for a bounded source tree: a SHA-256 over the admitted path set and each file's
  exact bytes, with ordinal-sorted paths, preserved case, unnormalized file bytes and the admission
  rule version inside the digest. It is not a commit id and cannot be exchanged for one.
- Multi-provider routing. A `Providers` entry now names the endpoint that answers a route and the
  environment variable holding its credential. One declared provider keeps using the host section
  exactly as before; two or more get no default at all, and each must supply its own endpoint and
  credential reference or the host refuses to start. A credential reference names a variable and never
  a value, because the configuration loader expands placeholders into the document it hashes and
  stores as a job's pinned snapshot. Embedding routes moved with the chat path.
- Optional one-hop route fail-over. A chat route may name another chat route as its `FallbackRouteId`,
  and a call that fails as `Unavailable` or `GenerationTimeout` is retried there once. Both calls share
  the primary's cancellation, both are charged, the failed call's accounting is made durable before the
  second may spend anything, a fallback does not itself fail over, and a fallback success does not
  clear provider backpressure. The declaration is validated while the host starts. No shipped route
  declares one.
- Optional route-level reasoning preference. A chat route may set `Reasoning` to `off`, `low`, `medium`
  or `high`. Which provider field carries it is explicit per-provider configuration
  (`ModelGateway:OpenAiCompatible:ReasoningModes`, one of `Disabled`, `ReasoningEffort` or
  `ChatTemplateKwargs`) and is never inferred from a model name. An absent value sends no
  reasoning-specific field, so an existing route's request shape is unchanged. Provider-reported
  reasoning token counts are recorded in `ModelCall` metadata; reasoning text is never deserialized.
- Payload retention. Aged signal payloads are compacted and attempt artifacts that are no longer a
  job's current attempt are reaped, each bounded per run. The Worker drives one pass every 15 minutes
  by default (`IncidentCompass:RetentionSchedule`), with default windows under
  `IncidentCompass:Retention` of 30 days for signal payloads and 7 days for attempt artifacts.
  Retention can be disabled, and a disabled host says so once at start rather than looking broken. A
  failure of one operation does not stop the other or the pump; cancellation ends the pass
  immediately.
- Every ledger event now carries a `payloadState` of `Retained`, `Reaped`, `NotReapable` or `None`,
  resolved at read time as a scalar so no event can be dropped by the resolution itself. Before this, a
  reference whose payload had been reaped rendered as exactly the same string as one still stored.
- Report model provenance. A published report carries the distinct combinations of call kind, role,
  route, configured provider and model that the backend recorded as having answered it, with a call
  count each, derived inside the publish transaction from that attempt's ledger rows. Absent, empty and
  populated are three different statements, and a forged value in model output is inert.
- Backend-owned report sentences for what a reader could not otherwise see: that evidence the report
  cites had values withheld by redaction, and that the attempt ran degraded because a call was answered
  by a route fallback. Each is appended when the backend recorded the fact and removed when it did not,
  so a model can neither assert nor suppress one.
- Memory corpus generations. A generation records the route, configured provider, model and dimensions
  that produced it. A changed embedding route is detected before any embedding call, again after the
  provider answers, and again inside the publish transaction, which refuses to commit a corpus holding
  more than one vector space. `memory rebuild` and `memory status` are Worker console commands, and
  `GET /api/v1/health/memory-corpus` reports the metadata-only view. A corpus built before this release
  reads as unrecorded rather than being assigned the currently configured provider.
- Model price administration. `incidentcompass.ai_model_pricing` now requires an `administered_by`
  value, stamps the change time in the database and discards a supplied one, refuses two overlapping
  intervals for one provider and model, and refuses deletes. Retirement is non-destructive and leaves
  every prior hour priced as reported.
- The fault response carries the job's last recorded outcome code for every status, and the time a
  retry becomes eligible while one is actually scheduled. The stored message stays behind; what crosses
  the boundary is a closed vocabulary.
- Action approvals can be listed by `faultId`, the exact inverse of the existing lookup from an
  external resource to its audit trail.
- The hourly cost rollup reports `estimatedUsageCallCount` and `estimatedUsageTotalTokens`, so a low
  spend figure is an answerable question rather than a mystery.
- Backend-authored untrusted-context delimiting in the orchestrator prompt. Incident context sits
  inside backend-authored boundary markers and every interpolated value is JSON-escaped, so an embedded
  newline or quote cannot close the block. This is prompt hygiene, not a security boundary.
- Worker output validation diagnostics. A reprompt emits a structured warning and a ledger entry on
  both the worker and orchestrator paths, the validator accumulates every violation of one answer up to
  a named bound and signals truncation, and the correction turn carries the full violation list
  together with the role schema. Only backend-authored text is surfaced.
- An opt-in triage quality evaluation harness. `evaluations/triage/corpus-v1.json` freezes five cases
  (known, unknown, insufficient, stale, adversarial) with three attempts each and pre-authored
  diagnosis, evidence, justified-refusal and tolerance criteria. `compose.evaluation.yml` starts an
  isolated stack with empty action grants and no external credentials, and
  `scripts/real-local-llm-smoke.ps1` drives the run and checkpoints a sanitized, bounded result after
  every attempt. It replaces the in-suite real-model smoke test, which was removed.
- One measured run published rather than described: `docs/evaluation-evidence.md` carries the numbers
  and their limits, and the per-attempt record is committed beside the corpus under
  `evaluations/triage/`.
- A bounded single-host operations path: `compose.production.yml`, `.env.production.example`,
  `docs/single-host-production.md`, and preflight, backup, restore and recovery-smoke scripts under
  `scripts/`.
- `MaxRetryDelaySeconds` for the chat and embedding clients, each under its own configuration section
  (default 5 seconds, configurable 1 through 3600). `Retry-After` delta and date values are honored up
  to that ceiling, which also caps the exponential fallback.
- `scripts/internal-reference-gate.ps1`, which fails when a private tracker identifier or an internal
  roadmap label appears in a tracked file, and runs in CI.
- A required CI container-image gate that builds the `api`, `worker` and `tester` demo images, and an
  aggregating `build` check that succeeds only when every job above it succeeded.
- A CI workflow that regenerates and pushes dependency lock files on Dependabot pull requests, so a
  central package bump no longer fails locked-mode restore in the projects Dependabot did not walk.
- A test that fails when reviewed documentation names a repository path that does not exist.
- Documentation index `docs/README.md`, integration configuration guide `docs/integrations.md`, public
  `CONTRIBUTING.md`, a Mermaid diagram of the governed path from signal to grounded report, the
  verbatim output of a mock demo run, and a state diagram of the action approval lifecycle.

### Changed

- Behavior change: the shipped ceilings were raised for local generation. The chat provider
  per-attempt timeout is now 300 seconds (was 30), the orchestrator `MaxWallClockSeconds` is 600 (was
  120), `analysis-chat` allows `MaxOutputTokens` of 8000 (was 2000), and the Development Worker lease is
  720 seconds (was 300). Embedding calls keep their own 30-second deadline. These are ceilings, not
  expected consumption. The gap between the per-call and per-investigation deadlines is deliberate: a
  stall surfaces as a retryable `provider_generation_timeout` rather than an immediate dead-letter.
- Behavior change: the configured `TimeoutSeconds` is now the authoritative deadline for one HTTP
  attempt on both OpenAI-compatible adapters. The typed `HttpClient` carries no separate deadline, so
  its framework default can no longer end a valid long-running attempt early. The accepted range for a
  configured timeout widened from 300 to 3600 seconds, and the chat client disables automatic redirects
  so a redirected POST is not silently replayed.
- Behavior change: provider failures are classified into application-owned kinds (`Unavailable`,
  `RejectedRequest`, `GenerationTimeout`, `OutputLimitReached`, `AmbiguousInterruption`,
  `TransportFailure`, `InvalidResponse`, `Unknown`) with explicit Worker dispositions. Only
  `Unavailable` enters provider-outage tracking and the delayed retry that costs no attempt. A rejected
  request, an output-limit exhaustion and an ambiguous post-dispatch interruption dead-letter
  immediately. A generation timeout, an invalid response, an unknown failure and an embedding transport
  failure consume the normal finite attempt budget. A transport failure counts as provably pre-dispatch,
  and therefore `Unavailable`, only for the connect, handshake and proxy-tunnel phases. Anything after
  dispatch stays ambiguous, so a generation that may already have been billed is never replayed.
- Behavior change: a model call's cost record names the configured provider that answered rather than
  the adapter that spoke the protocol, and records both. The pricing table is keyed on the configured
  provider, so every shipped price row had been seeded under a name no real call ever wrote and cost
  accounting had never once produced a figure. A row written before this release cannot be priced and
  stays unpriced rather than being attributed by guess. Spend is now built only from provider-reported
  token counts; estimated counts still add to token totals but never to money.
- Behavior change: report provenance grouping and the model-call completion log take the configured
  provider into their key as well, so two declared providers behind one adapter answering the same
  model name are no longer reported as one participant.
- Behavior change: reprompt exhaustion dead-letters instead of consuming a retry. Exhausted worker
  output corrections dead-letter as `worker_output_invalid` with a fixed bounded stored reason, and an
  exhausted orchestrator reprompt allowance dead-letters as
  `triage_budget_orchestrator_reprompt_limit_reached` instead of storing a generic code and spending an
  attempt. The exhausting turn emits its own warning before the throw.
- Behavior change: worker tool results are redacted before they are stored and before the model sees
  them. The persisted artifact, the immediate tool message, the tool failure message and the delegate
  result the orchestrator reads back all pass through one factory that redacts, canonicalizes and
  hashes, so the stored hash describes the stored bytes. A tool can no longer express a persisted
  artifact at all; it returns a draft. Source excerpts get no exemption, so a line matching the
  credential pattern loses its right-hand side.
- Behavior change: HTTP 501 and 505 responses are no longer retried on the embedding path. Both paths
  classify them as `RejectedRequest`, and the retry predicate and the terminal classification are
  derived from one rule. The embedding retry delay honors the configurable ceiling instead of a
  hardcoded five seconds, and the Tester's own retry loop gained an attempt cap.
- Behavior change: every tool-policy denial writes its reason as a stable code, optionally followed by
  a colon and a human detail. The `rate_cap` and `precondition` denial reasons change shape in the
  ledger. The public post-report denial vocabulary is derived from that code rather than from
  prefix-matching the reason text.
- Behavior change: `memory_search` fails closed when its configured `EmbeddingRouteId` names a route
  absent from the rehydrated configuration. The attempt stops with
  `triage_governance_memory_search_route_missing` before any embedding or retrieval call, instead of
  throwing an unhandled lookup failure. The memory seed service applies the same guard.
- Behavior change: a successful model call keeps its token accounting when the ledger batch fails, and
  a failed call retains returned provider usage without estimating. Unknown usage stays unknown rather
  than being recorded as zero.
- Behavior change: six model-name settings were removed because nothing read them:
  `ModelGateway:DefaultModel`, `ModelGateway:StrongModel`, `ModelGateway:CheapModel`,
  `ModelGateway:EvaluationModel`, `ModelGateway:AllowedModels` and `Embeddings:DefaultModel`. The route
  selects the model. They are no longer declared or validated, so a host that still sets them is
  ignored rather than rejected, and the values have no effect.
- `IncidentCompass:Application:FullPromptLoggingEnabled` and
  `IncidentCompass:Observability:AiRequestLogging:FailureMode` were removed from the environment
  sample. No code ever bound either one, and the sample now states the guarantee directly instead of
  naming a switch that does not exist.
- Behavior change: migration checksums are canonicalized. A new applied or failed record hashes each
  script name plus its SQL after removing one leading byte-order mark and normalizing CRLF or bare CR
  line endings to LF, so the value no longer depends on the checkout platform. The migrator also
  accepts the CRLF form of the frozen catalog text for a matching version and name, as a closed
  two-entry list rather than permission to accept arbitrary alternate hashes.
- Internal tracker identifiers and roadmap labels were removed from every tracked file, including the
  comments of six released migration scripts (`004`, `006`, `007`, `008`, `009`, `010`) and one
  user-facing exception message. All six belong to catalog migration version 1, whose single recorded
  checksum therefore changed on purpose. The migration scripts are frozen from this release forward;
  see `docs/versioning.md`.
- Behavior change: the orchestrator is now told what it is being asked for. The documentation-fit field
  is declared in the tool schema it receives, generated from the type the parser parses into; the
  per-document status is carried through the delegate result instead of being parsed away before it
  arrives; the refusal names the derived value; and the shipped instruction states the aggregation rule
  and derives its example rather than asserting one. The correction allowance is unchanged.
- The shipped role output schemas dropped the constructs the worker output validator cannot evaluate,
  and the memory, source and tickets instructions now name exactly which tool fields to copy, require
  bare JSON, forbid emitting null for a field the tool reported as null, and ship matched, no-match and
  failure examples whose codes are the ones the shipped configuration produces.
- The evaluation configuration's references name the shipped instruction and schema files by relative
  path, so it loads from its repository location rather than only from a Compose mount, and every
  discovered configuration is now proven to resolve every reference from its own directory.
- The promise that a rendered prompt never reaches a log is now a test rather than a comment: the
  provider adapters contain no output sink of any kind, and the test fails if one appears.
- Documentation was reorganized around a three-tier reading order, the quickstart became the single
  runnable path with the demo document keeping only what is demo-specific, and fourteen statements the
  repository made about itself that were wrong or incomplete were corrected, including the architecture
  contract's list of Application feature folders.
- The provider-failure-to-error-code map, the most-restrictive action execution mode and the reprompt
  rationale bound each have one representation instead of several. The stored reprompt rationale text is
  unchanged; the bound is now applied once, by the appender that owns the field.
- Behavior change: the Worker's three named claimed-task-set types are now thin per-pump log-event
  factories over one shared claimed-task set. A cancellation the host did not request is now logged as
  a warning on every pump; two of the three pumps previously swallowed it silently.
- `ProviderException` no longer carries an `HttpStatusCode`. Provider HTTP detail does not belong in
  an Application contract, and a non-HTTP adapter had no honest value for it; the normalized
  `ErrorCode` and `ProviderFailureKind` already carry what the status meant. No consumer read it.
- `ArchitectureTests` now checks `PackageReference` as well as `ProjectReference`, against an exact
  allowed set for `Domain` (none) and `Application` (FluentValidation plus abstractions-only
  `Microsoft.Extensions.*`). It also fails on a provider transport type named in either layer.

### Fixed

- Cost accounting had never produced a figure at all, because the recorded provider was the adapter
  rather than the configured one. See the configured-provider entry above.
- Memory retrieval silently returned nothing after an embedding model change. The seed pass compares
  files, and a model change does not change a file, so nothing was re-embedded while retrieval filters
  on the vector space the query was embedded in. A file that had also changed was re-embedded alone, so
  the active corpus genuinely held two vector spaces at once. The health check reported healthy
  throughout.
- The production Compose overlay did not re-declare the PostgreSQL credentials on its own service, so
  the demo fallbacks stayed in scope. The overlay now requires them, and a test fails if a fallback, a
  bare read or the demo password could reach a production run.
- An empty base-branch value reached every production Worker as an empty string from the Compose file
  and stopped the host from starting at all. Blank now means not configured and refuses at dispatch,
  while a real typo still stops the host at boot.
- A failed provider call in a remediation pass carried pending accounting that only the investigation
  attempt-failure path knows how to write, so those tokens would have been spent and never seen by the
  rollup. The workflow now writes the accounting before dead-lettering.
- The Tester container image failed to build after the evaluation harness added a corpus reference to
  the project file, because the Dockerfile copied the corpus only into the final stage. That broke the
  one-command demo path the README and quickstart advertise.
- The Docker-gated five-case evaluation contract test had never passed. It awaited the shutdown drain,
  which cancels in-flight jobs before awaiting them, read camelCase names from a PascalCase delegate
  result, and published a field the backend cannot derive.
- An evaluation deadline test was decided by platform timer granularity: its attempt deadline and its
  transient-retry cap were configured within about fourteen milliseconds of each other. It always passed
  on Windows and failed ten times out of ten on Linux, which is where CI and the release workflow run.
  The stub now makes the retry cap structurally unreachable, and the retry cap gained its own test.
- Nothing had ever validated a source or tickets worker output against its role schema, and the four
  integration fixture schemas still used rejected constructs, so a delegation using them would have
  thrown past the reprompt handler and burned the retry budget under a generic code.
- Two shipped tool instructions grouped a pair of outcome codes under a description that was true for
  one and false for the other. `ticket_search_no_safe_terms` and `source_frames_not_found` each now name
  the work that did and did not happen before the code was returned.
- Integration ingest helpers appended a hexadecimal token for uniqueness that the fingerprint mask
  collapses, so a second ingest of the same signal received no job id.
- The code-organization gate fired on a word inside a documentation comment rather than on the literal
  it exists to catch. It now skips comment lines, with a negative control proving it still catches the
  real thing.
- Dead surface was removed: an architecture test that scanned for strings present nowhere, a Domain
  port check that matched by substring, a helper reporting the wrong violation category for one of its
  callers, Worker types public only because a test project reached for them, and three error codes
  produced but asserted nowhere.
- A `delegate` naming a role the configuration does not hold is now a correctable refusal instead of
  a silent early return. It costs one bounded reprompt, is durable as an `orchestrator_reprompt:`
  `BudgetEvent`, and no longer echoes the model-supplied role back into the tool result. Previously
  the turn skipped the worker-budget check and every ledger append while still counting as a
  delegation, so a model looping on an unknown role spent the whole turn allowance invisibly.
- A tool rule's scope has one representation. The engine parses it once and denies a scope it does
  not evaluate (`unknown_rule_scope`); both fact readers are handed the parsed window instead of the
  configured string. The two readers previously interpreted an unrecognized scope differently - one
  narrowed it to the attempt, the other widened it to the job - on the two governance paths the
  engine exists to unify.
- The remediation workspace adapter no longer reports a logic defect inside the diff engine as
  `source_workspace_unavailable`, which also stopped the pass from reprompting. It catches only the
  filesystem's own answers; resolving the configured roots is guarded where that string work
  actually happens.
- A successful read of an existing branch answers `code_publication_branch_read` instead of
  borrowing the branch-created code, which is logged and persisted.
- The Telegram action adapter normalizes its transport failures like every other HTTP adapter here.
  A failure that provably preceded the request is `telegram_unavailable`; anything that may have
  been delivered stays `dispatch_outcome_unknown`. Previously both escaped into the dispatcher's
  catch-all as an in-doubt row a person has to settle.
- The PostgreSQL health check is answered over TCP rather than the Unix socket, so a service that
  waits on it no longer starts against a database that is still refusing connections. On a fresh
  volume the image entrypoint first brings up a temporary socket-only server to run its
  initialisation, and `pg_isready` reported ready against that while port 5432 was still closed.
  The API's first connection was refused, hosting failed to start, and only `restart: on-failure`
  brought it back; under load that second start did not fit the API's own health budget, so
  Compose declared the dependency failed and the production recovery test failed with it.

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
