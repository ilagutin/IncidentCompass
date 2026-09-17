# IncidentCompass 0.5.0 - local embeddings, sectioned memory, and limits that fit a slow model

IncidentCompass 0.5.0 changes three things an operator meets on the first day. Memory embeddings no
longer need an external embedding server: the Worker runs a pinned multilingual model in process and
installs it itself. A slow local chat model no longer fails an investigation at a short total
deadline: each provider call is bounded by what it is waiting for, chat answers are streamed by
default so a stall is visible as a stall, and the attempt as a whole gets a four-hour safety ceiling.
And an investigation now polices its own tool use and its own progress: every tool call has its own
execution limit, repeated calls and stalled progress are detected, a bounded recovery call gets one
attempt to unstick it, and a stalled investigation still ends with an honest report the backend writes
itself.

This is still reference-quality software with the single-host envelope described in
[Single-host production runbook](single-host-production.md). Nothing below widens that envelope.

## What changed

### The Worker is the only embedding host

The embedding client, the memory seed and resync pass, `memory rebuild` and the `memory_search` tool
are composed only by the Worker. The API keeps the corpus status and health readers, which need no
model, and no longer runs the corpus commands; `memory status`, `memory rebuild` and the new
`memory model` commands run in the worker container. The API image no longer carries the sample
runbooks, and the API service no longer sets a seed source directory it never used. An architecture
test fails if API source names an embedding adapter, the embedding port or the corpus command.

### An in-process embedding model is the shipped default

A new provider kind, `LocalOnnx`, runs `intfloat/multilingual-e5-small` inside the Worker with ONNX
Runtime and the model's own SentencePiece tokenizer. The model is MIT-licensed and pinned to one
Hugging Face revision; its int8 ONNX file and its tokenizer are each pinned by SHA-256. The adapter
applies the model's `query: ` and `passage: ` prefixes, caps input at 512 tokens, mean-pools,
normalizes and returns 384 dimensions. Every embedding request now states whether it carries a query
or a passage; the OpenAI-compatible and mock adapters ignore that field.

The shipped configuration routes `memory-embed` to a `local-embed` provider of kind `LocalOnnx`, and
both Compose files select that host provider unless `INCIDENTCOMPASS_EMBEDDINGS_PROVIDER` says
otherwise. The OpenAI-compatible embedding adapter stays available as an operator choice, set with
three values together (host provider, provider id and model), and the evaluation stack keeps it.

The model lives in the `embedding-models` volume at `/app/models`, mounted on the worker only. On
start, before the memory seed pass, the Worker installs or verifies it:

- An installed manifest wins. Both files are hashed again on every start, and a changed host default
  never replaces what is installed.
- A file already at its artifact path, for example one placed offline, is verified and used, never
  fetched over.
- A missing file is downloaded over HTTPS within a size limit to a temporary file, hashed while it is
  written and renamed into place only when the digest matches. The manifest is written last.
- A mismatch or failure is refused with a named code and never repaired. It does not stop the Worker.

A corpus embedded by the local model records its model as
`<model id>@sha256:<first 16 hex characters of the model file digest>`, so a model file replaced under
the same id is detected as an embedding route change. When the installed model cannot serve the
route, the Worker reports `memory_embedding_model_mismatch` (a model is installed but its id is not
the route's model) or `memory_embedding_model_unavailable` (no usable model is installed). Both publish
nothing and keep the previous corpus current, and `memory rebuild` refuses in either state because a
rebuild cannot repair it.

Two Worker commands manage the model. `memory model status` prints the installed model's id,
revision, license, encoded identity and digests beside the configured route and the corpus model, and
exits 1 on any mismatch. `memory model install` installs the configured model beside the current one
within the install timeout, keeps the replaced manifest as `manifest.previous.json` and never deletes
an installed file, so an install can be rolled back by restoring the previous manifest and restarting
the Worker. The runbook gives the install, offline install and rollback procedures.

`THIRD-PARTY-NOTICES.md` lists the model and the runtime packages with their licenses, and a test
fails if a shipped license is anything other than MIT or Apache-2.0. The model itself is not in this
repository or in any image built from it.

### A model that cannot serve the route is a configuration failure

While the installed model does not match the route, or no usable model is installed, every triage job
that reaches `memory_search` has its embedding call refused. That is now treated as a configuration
failure rather than a provider outage: the attempt stores `memory_embedding_model_mismatch` or
`memory_embedding_model_unavailable`, with the adapter's own code kept as the provider error code,
spends the ordinary attempt budget and is dead-lettered when that runs out. It never pauses job claims.

**A failed download or an install timeout of the local model now spends job attempts instead of
pausing claims.** Either one leaves no usable model installed, so a job that reaches `memory_search`
fails its attempt with `memory_embedding_model_unavailable` and the Worker keeps claiming work. A
model that is installed but fails to load, and an unreadable embedding configuration, still count as
provider outages.

### Memory documents are chunked by section

The seed pass no longer embeds a file as one chunk. It splits the frontmatter-free body on ATX
headings, ignoring heading-like lines inside fenced code, and caps each section by tokens on whole
lines, preferring paragraph boundaries, with a small overlap between adjacent chunks. The defaults
under `IncidentCompass:Memory:Seed:Chunking` are `MaxTokens` 448, `OverlapTokens` 48 and `MinTokens`
32. Token counts come from the installed model's own tokenizer for the local adapter, and from an
estimate of one token per four characters for the OpenAI-compatible and mock adapters, which expose
no tokenizer. The Worker refuses to start seeding when the chunk cap plus the prefix and sequence
markers would exceed the installed model's window.

Every chunk starts with its heading path, the ancestor headings joined with ` > `. The path is stored
in `memory_chunks.heading_path` and returned as `headingPath` in `memory_search` output. Chunks remain
the retrieval unit, so two sections of one runbook can both match. A heading path or a single line that
cannot fit the cap is refused rather than split or truncated, and the pass fails naming the seed
file's relative path without quoting it.

Each corpus generation records its chunk policy in `memory_corpus_generations.chunk_policy`, for
example `v1;max=448;overlap=48;min=32;exact`. A changed chunking setting publishes nothing, keeps the
previous corpus current and reports `memory_chunk_policy_changed` until `memory rebuild` publishes a
generation under the new policy. Migration `036-memory-chunk-structure.sql` adds both columns as
nullable and rewrites no row.

**A corpus seeded before migration 036 keeps its whole-file chunks and is re-chunked only after
`memory rebuild`.** Such a generation has no recorded policy, which reads as unrecorded rather than as
a different policy, so it stays current and its unchanged files are not re-chunked by the ordinary
seed pass.

### The local models were measured, and the shipped settings stay

An opt-in benchmark, run only when `INCIDENTCOMPASS_EMBEDDING_BENCHMARK` is set, runs the retrieval
corpus through the real local adapter for `multilingual-e5-small` and `multilingual-e5-base`, with the
English queries and Polish and Russian renderings that reuse the same relevance labels. It reports
the production `memory_search` pipeline across `MinScore` values, the raw vector ranking and query
embedding latency. The deterministic gates keep the mock embedder.

The measurement keeps `multilingual-e5-small` as the shipped model and `MinScore` at 0.25. On that
model the floor removes nothing: every relevant match on the benchmark scored well above it, and so
did unrelated chunks. What keeps unrelated chunks out is the reranker's lexical coverage rule, which
requires at least half of the query's words to appear in the chunk text. That has a consequence
stated in [Trade-offs](trade-offs.md): **a query written in another language than the corpus usually
finds nothing**, even when the multilingual vector ranks the right chunk near the top, unless enough
words from the corpus language, such as error types or service names, appear in the query.

### Provider calls are bounded by phase, and the attempt by a long ceiling

The single chat `TimeoutSeconds` bounded a whole HTTP attempt, so a slow local model either needed a
very large value or failed as a timeout while still generating. The OpenAI-compatible chat adapter now
applies three limits to each HTTP attempt, under `IncidentCompass:ModelGateway:OpenAiCompatible`:

- `ConnectTimeoutSeconds`, default 30, for establishing the connection;
- `FirstOutputTimeoutSeconds`, default 600, from dispatch until output starts;
- `StreamInactivityTimeoutSeconds`, default 600, for silence once output has started, restarted on
  every output event, so a long answer that keeps producing is not cut off.

Each retry gets fresh limits. A limit that fires ends the call as a generation timeout recorded under
its phase, `provider_connect_timeout`, `provider_first_output_timeout` or
`provider_stream_inactivity_timeout`; `provider_generation_timeout` remains for an HTTP 408 answer. A
failure after the response started, or a cancellation none of the limits explains, is
`provider_dispatch_outcome_unknown` and is not replayed, because the provider may already have
generated.

`Orchestrator.Budget.MaxAttemptDurationSeconds` is the ceiling on one investigation attempt. Absent
means 14400 seconds, `0` disables it, and the shipped configuration sets 14400 where it previously set
a 600-second `MaxWallClockSeconds`. It is a safety net against a run that never ends; the call limits
are what catch a stalled call.

Each model call now asks the provider for at most the smaller of the route's `MaxOutputTokens` and the
tokens left in the attempt budget after the estimated prompt, sent as `max_tokens`. A call with nothing
left is refused before dispatch, and a route fallback skipped for that reason is recorded as a
`max_tokens_reached_before_call` budget event.

### Chat completions are streamed by default

The chat client requests `stream: true` with `stream_options: { "include_usage": true }` and assembles
content, tool calls, finish reason and usage from server-sent events into the same completion a JSON
body produces, which then goes through the same validation. The response's content type decides how
it is read, so a provider that ignores `stream` and answers with JSON keeps working.
`IncidentCompass:ModelGateway:OpenAiCompatible:Streaming=false` restores the previous request exactly.

A stream fails closed. An `error` event, a stream that ends before a finish reason and a body that
breaks off are `provider_dispatch_outcome_unknown` and are never replayed; only the provider error
code of an error event is kept, never its message. A malformed or inconsistent tool-call fragment is
`invalid_response`. Keep-alive comments do not count as output, so a provider that holds a connection
open while producing nothing is cut off at the inactivity limit. A chat response is capped at 32 MiB
and a single event line at 4 MiB characters, both ending the call as `provider_response_too_large`.
[Model gateway](model-gateway.md) lists the known provider incompatibilities, including
OpenAI-compatible layers that stream tool-call fragments without `index`, which need
`Streaming=false`.

### Every tool call has an execution limit

Each tool in the triage configuration may set `TimeoutSeconds`, from 1 through 3600. An immediate
worker tool that sets none is bounded at 120 seconds; an external action that sets none uses the
Worker's `IncidentCompass:ActionDispatch:AdapterTimeoutSeconds`, 30 by default. The shipped
configuration sets none, so its hash is unchanged.

An immediate tool call runs under three bounds at once, and the outcome names the bound that fired:

- its own limit records a `Failed` tool result with `tool_execution_timeout`, and the worker continues
  with that limitation;
- the attempt ceiling ends the attempt with `triage_budget_wall_clock_reached_during_call`, as during a
  model call;
- shutdown propagates as cancellation without a ledger write.

Any other exception records a `Failed` tool result with `tool_execution_failed`, naming the tool, and
propagates unchanged. The executor stops waiting as soon as a bound fires, even for a tool that ignores
cancellation; such a read-only tool is abandoned and may keep running until it returns.

An external action resolves its limit before it is claimed, so the adapter deadline and the claim
deadline use the same value. An action stopped after its claim but before its adapter was invoked,
shutdown included, now closes as `dispatch_not_invoked`; after invocation the outcome stays
`dispatch_outcome_unknown` and the action is never re-sent.

### An investigation that stops making progress is detected and ended honestly

**Repeated calls.** A worker tool call with the same tool and the same canonical arguments, or a
delegate with the same role and the same task, that has already been repeated
`Orchestrator.Budget.MaxEquivalentCalls` times in a row with an unchanged result is refused before it
runs (with the default 2, the first call and two identical repeats run and the fourth call is
refused). The worker receives a tool failure with status `NotExecuted` and code
`repeated_call_without_new_evidence`; the orchestrator receives the same code as a delegate result.
The attempt is not failed and no reprompt is charged. "The same result" ignores the fresh artifact ids
a call writes, so two identical memory searches that find the same runbook are the same result, and a
result that changed resets the count. Once a call is refused it is not run again in that attempt, so
data behind that exact call is not re-read later. The check runs after the policy decision, so
`rate_cap` is unchanged. A worker that proposes two refused calls in a row is stopped without an
answer: the orchestrator receives `worker_stopped_repeating`, no worker output is stored and no schema
reprompt is charged.

**Progress.** An orchestrator turn makes progress when it adds evidence not seen before in the attempt
or changes the candidate classification. Time is not an input, so a slow model that keeps producing is
never a stalled one. When consecutive turns without progress go past
`Orchestrator.Budget.MaxTurnsWithoutProgress` (default 4, range 2 to 32):

- with a recovery left (`Orchestrator.Budget.MaxRecoveries`, default 1, range 0 to 3), the backend
  makes one diagnostic call on the orchestrator route with the new call kind `recovery`, through the
  same budgets, provider limits and `ModelCall` accounting. It is offered no tools and sees only a
  backend summary of counts, role and tool names, the evidence count, the candidate classification
  and the fixed task text. Its answer, bounded to 2000 characters, reaches the orchestrator as a user
  message marked as a suggestion, and the orchestrator continues through its own tools. The recovery
  call cannot start another recovery. A provider outage or another failure a later attempt could get
  past goes back to the job runner as for any call. A failure that would repeat for the same request
  has its accounting written and uses up that recovery; the attempt continues, with a fixed backend
  note telling the orchestrator to change its next call, only when another recovery and another
  window remain, and otherwise ends as below.
- with none left, when the remaining turns or workers could not hold another window, or when the token
  budget or context window leaves no room for the recovery call, the backend publishes its own report
  through the same grounding path: status `InsufficientEvidence`, classification `Unknown`, confidence
  `Low`, a fixed summary and next action, and a fixed limitation that names why it stopped. It cites
  only the trigger signal, and on a re-triage the recurrence state. The job succeeds, and the report's
  `ReportPublished` ledger rationale opens with `backend_authored: `, which a model-authored report
  cannot produce. Reserved backend text in a model-authored report is refused or removed.

Running out of turns or workers inside a stall the attempt detected, and that no progress has ended
since, also ends with the backend report; otherwise both limits dead-letter as before. A backend
report the repository refuses dead-letters as `triage_no_progress_termination_failed`.

`Orchestrator.RecoveryInstructions` optionally points at other recovery instructions; absent, the
built-in text shipped as `config/instructions/recovery.md` is used, and a configuration that does not
set any of the new keys keeps its hash. Every intervention writes a `BudgetEvent` with the
`no_progress:` prefix (`repeated_call`, `turns_without_progress`, `worker_stopped`, `recovery`,
`recovery_failed`, `terminated`) and a log event.

### Model answers that cannot be used are refused sooner

- A completion the provider cut off at its output ceiling is refused, on the JSON path and the streamed
  path alike. Any answer whose first choice reports `finish_reason: length` is
  `provider_output_limit_reached`, with or without partial content and with or without tool calls: the
  partial text is discarded and never reaches a caller, and a tool call whose arguments the ceiling cut
  through is reported under that code instead of as an invalid tool call. It previously became a normal
  completion as soon as it carried any text or tool call, so half a report or half a remediation diff
  could be used as if it were the whole answer. The job dead-letters without a retry, as an output limit
  already did, and the operator's fix is a higher route `MaxOutputTokens` or a smaller task.
- `publish_report` accepts exactly one argument shape per call: `report_json` alone, `report` alone,
  or a bare report. It previously took the first wrapper it found. `summary` (4000 characters),
  `recommendedNextAction` (2000), `limitations` (20 items of 1000 characters), `evidence` (50 items)
  and `quote` (1000) are refused past their limit rather than truncated, with a reprompt that names
  the limit, and the tool description states the same limits. They are not schema keywords, because
  grammar-constrained local runtimes expand large length bounds into very large grammars.
- A role output schema that types a secret-named property as an object, number or boolean is refused
  when the configuration loads and in `config validate`, naming the role, the schema path and the rule
  that matched. Redaction replaces such a value with a string, so the output used to validate and then
  fail to parse on every attempt. A mismatch the load check cannot see, for example behind a `$ref`,
  now ends the delegate as a non-retryable invalid worker output.

### Other fixes

- A remediation pass whose model call the provider answered, but whose ledger accounting failed, was
  dead-lettered as `remediation_model_call_failed`, which was false. It now writes the owed row and
  dead-letters as `remediation_answer_unrecorded`: the call is paid for, the answer is discarded and no
  diff is kept. If that write fails too, it dead-letters as `remediation_model_call_accounting_pending`.
  Neither is retried, because a retry would pay for the model call again.
- The production API read memory health under a seed scope the Worker never wrote. Production Compose
  now gives both services the seed tenant and owner from one shared block.

### Repository

- The Dependabot lock-file workflow is split into a `restore` job with no permissions and no secret,
  and a `push` job that runs no `dotnet` command, validates every uploaded file against an allow-list
  of tracked `packages.lock.json` paths and pushes without force. No token is reachable from the job
  that runs restore.
- The `push` job uses a short-lived GitHub App installation token when the App secrets are set. The
  personal access token is a deprecated fallback, and the job summary names the credential that
  pushed. [Versioning and release flow](versioning.md) documents the setup.

## Upgrade notes

**The first Worker start downloads the embedding model.** A Worker starting on an empty
`embedding-models` volume downloads the model file, about 118 MB, and its tokenizer, about 123 MB in
total, over HTTPS from `huggingface.co`, unless the files were placed in the volume offline beforehand.
Its start waits for that for up to `IncidentCompass:Embeddings:LocalOnnx:InstallTimeoutSeconds`, 900
seconds by default. Plan for the Worker's memory to grow by roughly the model file's size plus 100 to
200 MB; that is a planning figure, not a measurement. `down --volumes` deletes the model volume, and
the backup script does not back it up because its contents are reproducible from the pinned source.

**An existing deployment that embedded through an OpenAI-compatible server must choose.** The Compose
default is now the local model. To keep the server, set `INCIDENTCOMPASS_EMBEDDINGS_PROVIDER` to
`OpenAICompatible`, `INCIDENTCOMPASS_EMBEDDINGS_PROVIDER_ID` to `local-oai` and
`INCIDENTCOMPASS_EMBEDDINGS_MODEL` to that server's model id. To move to the local model, set
`INCIDENTCOMPASS_EMBEDDINGS_MODEL` to `intfloat/multilingual-e5-small` (production Compose requires the
variable; only `docker-compose.yml` alone falls back to that value when it is unset), and run
`memory rebuild` on the Worker once the model is installed: until then the corpus was built under
another route, and `memory_search` cannot reach it.

**Changing the route model follows an order, and a job caught across it dead-letters.** A job is pinned
to the route model of the API that created it, and the Worker refuses an embedding call whose model is
not the one it has installed. The procedure in
[Single-host production runbook](single-host-production.md), "Local embedding model", therefore runs
every one-off command with `--no-deps`, so `docker compose run` does not recreate the running API at a
moment the procedure did not choose, and goes in this order: install the model, let the queue drain and
stop the Worker, change `INCIDENTCOMPASS_EMBEDDINGS_MODEL` and recreate the API alone, run
`memory rebuild`, recreate the Worker. Installing a model leaves the previous corpus retrievable, and
jobs that arrive while the Worker is stopped wait and complete on the new corpus. What remains is a job
created before the API was recreated and not finished before the Worker restarted: its snapshot names
the previous model, so its `memory_search` call is refused, it spends its attempts and is dead-lettered
with `memory_embedding_model_mismatch`.

**Run `memory rebuild` to get section chunks.** A corpus seeded before migration 036 keeps its
whole-file chunks and reports current; it is re-chunked only by `memory rebuild`. Any later change to a
chunking setting also needs `memory rebuild` before it takes effect.

**Two configuration keys are deprecated.** Both keep working under the old name until a release that
states their removal, with a warning naming the new key, and setting both names is an error.

- `Orchestrator.Budget.MaxWallClockSeconds` is replaced by `MaxAttemptDurationSeconds`. A configuration
  file or stored snapshot that sets only the old key keeps its explicit value as the ceiling. Loading
  such a configuration file logs warning event 2701; rehydrating a stored snapshot for a queued job
  does not. Setting both keys is a load error, for a rehydrated snapshot as well as for the file.
  Rename the key to clear the warning, and consider raising a value chosen as a short total deadline
  before the provider call limits existed.
- Chat `IncidentCompass:ModelGateway:OpenAiCompatible:TimeoutSeconds` is replaced by
  `FirstOutputTimeoutSeconds`. When only the old key is set, its value becomes the first-output limit
  (warning event 2801 at host start). The warning and the both-set check run only when the chat
  provider is OpenAI-compatible. The old key no longer bounds reading the body, which the inactivity
  limit now does. The embedding section's `TimeoutSeconds` is not deprecated.

**A stalled investigation now ends with a report instead of running to the turn limit.** A job whose
orchestrator keeps making turns without new evidence gets one recovery call and then succeeds with a
backend-authored `InsufficientEvidence` report, where it previously ran until `MaxTurns` and
dead-lettered. Set `Orchestrator.Budget.MaxRecoveries` to 0 to skip the recovery call, or raise
`MaxTurnsWithoutProgress` to give a slow-converging model more room. A model that repeats the same call
with the same result is now refused after two repeats, where only `rate_cap` bounded repeats of a call
before.

**Streaming is on by default.** Set `IncidentCompass:ModelGateway:OpenAiCompatible:Streaming` to
`false` for a provider that rejects `stream` or `stream_options`, or that streams tool calls in a shape
this adapter refuses.

**The Dependabot personal access token is deprecated.** Where `DEPENDABOT_LOCKFILE_TOKEN` is configured,
create the GitHub App described in [Versioning and release flow](versioning.md) and store
`DEPENDABOT_LOCKFILE_APP_ID` and `DEPENDABOT_LOCKFILE_APP_PRIVATE_KEY` as Dependabot secrets.

## Defaults and compatibility

- The API remains under `/api/v1`, and no HTTP property was added, removed or renamed. The `state` of
  `GET /api/v1/health/memory-corpus` can now also read `EmbeddingModelMismatch`,
  `EmbeddingModelUnavailable` or `ChunkPolicyChanged`; `RebuildRequired` is false for the two model
  states, which a rebuild cannot repair.
- One migration was added, `036-memory-chunk-structure.sql`, catalog version 27. It adds two nullable
  columns and rewrites nothing, so a 0.4.1 database upgrades in place.
- New error codes: the three phase timeouts above, `remediation_answer_unrecorded`,
  `memory_embedding_model_mismatch`, `memory_embedding_model_unavailable`,
  `memory_chunk_policy_changed` and the local model install codes listed in
  [Model gateway](model-gateway.md), `tool_execution_timeout`, `tool_execution_failed`,
  `dispatch_not_invoked`, `repeated_call_without_new_evidence`, `worker_stopped_repeating` and
  `triage_no_progress_termination_failed`. New log
  events: 2701 and 2801 for the deprecated keys, 3213 for a skipped fallback whose budget event could
  not be recorded, 3305 to 3309 and 3521 for tool execution limits, and 3404 to 3411 for repetition,
  progress, recovery and termination.
- New optional configuration keys: `Tools.<id>.TimeoutSeconds`, `Orchestrator.Budget.MaxEquivalentCalls`,
  `MaxTurnsWithoutProgress`, `MaxRecoveries` and `Orchestrator.RecoveryInstructions`. The shipped
  configuration sets none of them. `ModelCall` rows gain the call kind `recovery`, and tool-call
  telemetry gains the outcome `refused`.
- Configuration validation is stricter in two places: a role output schema with a secret-named
  non-string property, and a configuration that sets both names of a deprecated key. Neither affects
  the shipped configuration.
- An embedding call still writes no `ModelCall` row, whichever adapter serves it. The local model has
  no provider to bill; its cost is Worker CPU and memory.
- Automated tests do not call a real model or embedding provider. The local model is exercised against
  a tiny generated fixture model; one integration test runs the real pinned model, downloading it when
  its cache is empty, where the Docker-backed tier is required (CI or
  `INCIDENTCOMPASS_REQUIRE_DOCKER_TESTS`).

## Not in this release

- Semantic progress detection. Repetition and progress are judged from result identities and the
  candidate classification, so a model that loops through reworded tasks or calls is not caught as
  repeating.
- Cross-language retrieval. The multilingual model embeds other languages, but the lexical coverage
  rule means a query in another language than the corpus usually finds nothing.
- Accounting of external embedding calls. An embedding call served by an OpenAI-compatible server
  still appears in no count, token total or spend figure.
- Requeueing a job across a change of the embedding route model. A job created before the route model
  changed and still unfinished when the Worker restarts dead-letters with
  `memory_embedding_model_mismatch`; the documented order keeps that to jobs already in flight, see the
  upgrade notes.

## Verification

The release gate runs locked-mode restore, build with zero warnings and zero errors, formatting
verification, the code-organization gate, the package-vulnerability gate and the internal-reference
gate.

`dotnet test --solution IncidentCompass.slnx` runs the deterministic unit and integration suites,
including the PostgreSQL-backed integration tests through Testcontainers with
`INCIDENTCOMPASS_REQUIRE_DOCKER_TESTS` set.

On the release tree the full solution run reported 2628 tests: 2624 passed, 0 failed and 4
skipped. The skips are three symbolic-link tests the Windows test process cannot create links for
and the explicit OpenAPI baseline regeneration, which only `scripts/update-openapi-baseline.ps1`
runs.
The skips are three symbolic-link tests the Windows test process cannot create links for
and the explicit OpenAPI baseline regeneration, which only `scripts/update-openapi-baseline.ps1`
runs.

The suites were also run on Linux in the .NET 10 SDK container, since CI and the release workflow
run on Linux. There, every test passed except those that drive the Docker CLI and Compose against the
host daemon from inside the container, which that setup cannot serve; they run in CI. A fresh-host Compose check
exercised the shipped defaults, and a scripted streaming provider check exercised the streamed chat
path.

What that does not cover:

- No automated test reaches a real chat provider or an external embedding server. The real local
  embedding model runs in one integration test in the Docker-backed tier and in the opt-in benchmark;
  the benchmark is not part of any gate.
- The Docker-backed integration coverage is enforced by an opt-in environment variable rather than by
  default.
- The streaming checks use scripted providers. Behavior against a specific provider's stream is
  covered only by the incompatibilities documented in [Model gateway](model-gateway.md).
- Tool execution limits, repetition detection, bounded recovery and honest termination are exercised
  with scripted model clients and a manual time provider, not against a real model.
