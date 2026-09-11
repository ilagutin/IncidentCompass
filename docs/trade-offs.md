# Trade-offs

This document records intentional choices and their costs.

## Contents

Testing and verification

- [Mock Model vs Real Model In Tests](#mock-model-vs-real-model-in-tests)
- [Real-Model Smoke History](#real-model-smoke-history)
- [Test Fault Seams Live In Production Code](#test-fault-seams-live-in-production-code)

Privacy and access

- [Full Prompt Logging vs Privacy](#full-prompt-logging-vs-privacy)
- [Configurable Redaction Is Best Effort](#configurable-redaction-is-best-effort)
- [Source Excerpts Are Redacted Like Everything Else](#source-excerpts-are-redacted-like-everything-else)
- [Pseudonymization Salt Rotation](#pseudonymization-salt-rotation)
- [Payload Retention Is Not Data Governance](#payload-retention-is-not-data-governance)
- [Simple Access Control vs Enterprise RBAC](#simple-access-control-vs-enterprise-rbac)
- [Local Tenant Partition And API-Key Mapping](#local-tenant-partition-and-api-key-mapping)

Structure, dependencies and versioning

- [Domain Records with Application-Owned Behavior](#domain-records-with-application-owned-behavior)
- [Reference Implementation vs Framework](#reference-implementation-vs-framework)
- [Internal Dispatcher vs MediatR](#internal-dispatcher-vs-mediatr)
- [FluentValidation vs Custom Validators](#fluentvalidation-vs-custom-validators)
- [.NET 10 LTS vs Older Targets](#net-10-lts-vs-older-targets)
- [`0.x` vs `v1.0.0`](#0x-vs-v100)
- [Raw String Identifiers vs Strongly-Typed Value Objects](#raw-string-identifiers-vs-strongly-typed-value-objects)
- [Canonical Migration Checksums vs Ledger Rewriting](#canonical-migration-checksums-vs-ledger-rewriting)
- [Two Compose Naming Styles Are Kept](#two-compose-naming-styles-are-kept)

Budgets, providers and the investigation loop

- [Sequential Ledger-Backed Governance](#sequential-ledger-backed-governance)
- [Token Budget Overshoot](#token-budget-overshoot)
- [Local-Safe Ceilings Allow Slower Generation](#local-safe-ceilings-allow-slower-generation)
- [Provider Retries Prefer Bounded Uncertainty](#provider-retries-prefer-bounded-uncertainty)
- [Budget And Governance Exhaustion Dead-Letters Instead Of Retrying](#budget-and-governance-exhaustion-dead-letters-instead-of-retrying)
- [Provider Backpressure Is Process-Local](#provider-backpressure-is-process-local)
- [Renewable Worker Leases Require Cooperative Calls](#renewable-worker-leases-require-cooperative-calls)

Memory, grouping and evidence

- [File-Backed Memory Is The Write Path](#file-backed-memory-is-the-write-path)
- [Documentation Fit Is Evidence Classification](#documentation-fit-is-evidence-classification)
- [Memory Embedding Model Changes Require Re-Embedding](#memory-embedding-model-changes-require-re-embedding)
- [Bounded Memory Reranking Instead of Database Full-Text Search](#bounded-memory-reranking-instead-of-database-full-text-search)
- [Deterministic Grouping Is Not Incident Correlation](#deterministic-grouping-is-not-incident-correlation)
- [Re-triage Reuses Untrusted History](#re-triage-reuses-untrusted-history)
- [Grounded Evidence vs Correct Conclusions](#grounded-evidence-vs-correct-conclusions)

Governance and external actions

- [One Live Tool Policy Path](#one-live-tool-policy-path)
- [Read-Only Cost Rollup Uses Operator-Maintained Pricing](#read-only-cost-rollup-uses-operator-maintained-pricing)
- [Hand-Edited Prices Are Constrained, Not Replaced By An API](#hand-edited-prices-are-constrained-not-replaced-by-an-api)
- [Cost Alerting Belongs To The Operator's Own Tooling](#cost-alerting-belongs-to-the-operators-own-tooling)
- [Durable Evaluation Queue Is Not A Second Action Outbox](#durable-evaluation-queue-is-not-a-second-action-outbox)
- [At-Most-Once Action Dispatch Prefers Visible Uncertainty](#at-most-once-action-dispatch-prefers-visible-uncertainty)
- [Compact External Projection, Not General Reconstruction](#compact-external-projection-not-general-reconstruction)

## Mock Model vs Real Model In Tests

Real model calls are expensive and nondeterministic. Automated tests use mock clients by default.

## Real-Model Smoke History

These runs were opt-in, non-gated local measurements against an OpenAI-compatible endpoint. They
measure whether the governed loop reaches its terminal step, not answer quality. The deterministic
release gate remains the mock-backed demo plus the automated tests.

| Configuration | Model | Scenario | Reach rate |
|---|---|---|---|
| Before bounded reprompts | qwen2.5-14b-instruct | publish_report | 0/3 |
| Before bounded reprompts | qwen/qwen3.6-27b | publish_report | 0/1 |
| Bounded reprompts | qwen2.5-14b-instruct | publish_report | 3/3 |
| Bounded reprompts | qwen/qwen3.6-27b | publish_report | 1/1 |
| Memory tools | qwen2.5-14b-instruct | delegate to memory to memory_search to publish_report | 5/5 |
| Memory tools and grounding | qwen2.5-14b-instruct | delegate to memory to memory_search to grounded publish_report | 3/5 |

The bounded-reprompt `3/3` result was measured with `Orchestrator.Budget.MaxReprompts: 2`; the earlier
baseline predates bounded reprompts.

These rates were recorded on 2026-07-02 and 2026-07-03, against the development build that shipped as
`v0.1.0` on 2026-07-04, the release that introduced bounded orchestrator and worker reprompts, the
governed `memory_search` tool and grounded report publication. The table is kept because it is the
measured evidence behind those decisions, including the honest regression it records: adding
grounding moved the memory trajectory from `5/5` to `3/5`.

It is a historical record, not a statement about the current build. Reprompt handling and
worker-output validation have changed since `v0.1.0`, so a rerun today would not be expected to
reproduce these rates. Read the numbers only as reach rates for the terminal step on the build and
date named above - not as a release gate, not as a measure of report quality, and not as a
comparison between the two models.

## Test Fault Seams Live In Production Code

Five interfaces exist in `src` for one reason: integration tests need to crash the process at an
exact statement inside a database transaction. `ITriageToolResultCommitFaultInjector`,
`ITriageReportFinalCommitFaultInjector`, `IActionApprovalTransactionFaultInjector`,
`ITriageReportPublicationIntentFaultInjector` and `IPostgresMigrationFailureInjector` are all
`internal`, all live under a feature-local `Testing/` folder, and are always bound to their no-op
default in production composition.

The obvious cleanup is to delete them and let tests wrap the real service in a decorator. That does
not work here, and the reason is worth stating plainly. Every hook sits *between* two statements of
one transaction: between the tool artifact insert and its `ToolResult` ledger insert, between the
terminal fault update and `ReportPublished`, between the action row and its provenance and ledger
rows, between the publication intent insert and the commit that publishes the report. A decorator
wrapped around `ITriageToolResultCommitter` or `ITriageReportRepository` can only throw before the
call or after it, and by then the transaction has already committed or rolled back as a unit. It can
prove that a failure is visible; it cannot prove that two writes roll back *together*, which is the
invariant these tests exist for and the reason the approval and publication code is shaped the way
it is. The migration seam fails for a different reason: one `MigrateAsync` call applies every pending
migration, so a decorator can only fail the whole run, never version 15 of a catalog while leaving
1 through 14 applied and 15 recorded as `Failed`.

None of these seams wraps a whole operation. Their cost is explicit:
these are testability hooks in production code, they add a constructor parameter to eleven
PostgreSQL adapters, and a reader who does not know why they exist could mistake them for dead code.
The mitigations are that the types are `internal` and invisible outside the assembly, the folder
name says what they are, each interface carries an XML comment naming the fault it simulates and
stating that production always gets the no-op, and the DI registrations are grouped into named
`AddInvestigationTestFaultSeams` / `AddPersistenceTestFaultSeams` methods instead of being scattered
among real services. The seams preserve the transaction-level partial-failure checks.

## Full Prompt Logging vs Privacy

Full prompt logs help debugging but may leak sensitive data. Default logging is metadata-only.

## Configurable Redaction Is Best Effort

Built-in and configured redaction rules reduce exposure before persistence and model calls, but a
pattern list cannot prove that all secret and PII formats are covered. New realistic data sources must
add regression fixtures for their known sensitive fields, and operators should keep full prompt/body
logging disabled.

The property-name denylist is the same kind of compromise. Segment matching catches real spellings
such as `x-api-key` and `user_password_hash` without redacting `session_id` or `key_count`, but it
accepts both directions of error: a name such as `token_count` is redacted although it holds no
secret, and a plural such as `cookies` or an unlisted vendor word is missed. The exact rule is stated
in `docs/security-model.md` so an operator can predict it and add configured attribute keys for the
names it does not know. A configured pattern that exceeds its 200 ms match timeout also destroys the
whole field rather than risk emitting a value redaction did not finish cleaning.

## Source Excerpts Are Redacted Like Everything Else

`source_lookup` returns application code, and the report's whole value is that it is grounded in that
code. Redacting it therefore costs something real, so the rules were checked one at a time against
ordinary source before the decision was made:

- the AWS access-key rule (`AKIA` plus sixteen upper-case characters) effectively never fires on code
  that does not contain a key;
- the prefixed-token rule needs `sk-`, `glpat-`, `xox?-`, `gh?_` or `github_pat_` at a word boundary,
  so identifiers such as `risk-scored` or `task-runner` do not match it;
- the bearer rule can fire on prose: a comment reading `Bearer authentication` loses the word after
  `Bearer`, because the rule cannot tell a documented header name from a real token;
- the connection-string rule is the expensive one. It matches `password` or `pwd` followed by `=`, so
  a line such as `if (request.Password == expectedHash)` loses everything after the name, and
  `var password = ReadFromVault();` loses its right-hand side.

The choice is to redact source excerpts with exactly the same pass as every other payload, and to
accept the last two costs. The reasoning is that a credential in a prompt is a worse failure than a
mangled line, the LLM is not a security boundary, and a rule that skipped source would make
`redacted_payload` mean something different depending on which tool wrote the row.

What was changed to make that affordable: the connection-string rule's tail is bounded by line breaks
as well as by `;`, so its pattern is `(password|pwd)\s*=\s*[^;\r\n]+` rather than `[^;]+`. Before
that, the negated character class also matched newlines, so on a multi-line excerpt with no later `;`
a single match could swallow every remaining line. The damage is now confined to the one line that
looks like a credential, the surrounding lines and the rest of the line after the `;` still reach the
model, and `ToolArtifactRedactionTests` asserts exactly that.

That narrowing is not free, and the loss is on the redaction side. A credential value that continues
onto the next line is no longer fully removed. The verified case is backslash continuation, the form
`.env`, `.properties`, shell scripts and Dockerfiles use:

```
DB_PASSWORD=hunter2\
supersecret-tail
```

The old rule ran past the newline and took `supersecret-tail` with it. The new one stops at the line
break: `DB_PASSWORD=[REDACTED]` is written, and `supersecret-tail` survives into the payload. This is
knowingly accepted rather than overlooked. A rule that keeps consuming lines destroys a whole source
excerpt every time it fires on ordinary code such as `if (request.Password == expectedHash)`, which
is the common case; a continued credential line inside a retrieved payload is the rare one, and the
tail that survives is a fragment with its name already gone. `SecretRedactorTests` pins both halves
so neither can change without a decision. Operators who need a different balance can add configured
patterns; they cannot turn the built-in rules off.

## Pseudonymization Salt Rotation

User identifiers can be replaced with stable HMAC-SHA256 pseudonyms so later blast-radius logic can
count distinct users without storing raw identifiers. The host-only salt is intentionally outside the
snapshotted triage config. Rotating it breaks continuity with older pseudonyms; running without it
fails safe to redaction and therefore loses distinct-user counting.

## Payload Retention Is Not Data Governance

Two lifecycle operations shorten how long raw payloads stay readable: aged signal payloads are
emptied, and triage artifacts belonging to an attempt that is no longer their job's current attempt
are deleted once they are past an age threshold. The mechanics and the full exclusion list are in
`docs/security-model.md`. What is recorded here is what they deliberately are not.

They are not a retention policy over records. Nothing removes a signal row, a fault, a job, a report
or a ledger entry. Two of those are not merely unimplemented: `faults.trigger_signal_id` references
the signal row, so deleting a signal would take the trigger away from the fault it opened, and
published reports are immutable by trigger, which rejects UPDATE and DELETE unconditionally. Report
retention would mean weakening that trigger, and the immutability is worth more here than the disk.
The ledger is the audit trail the whole exercise is meant to leave intact, so it is out of scope by
intent rather than by obstacle.

They do not reap failed attempts, because a failed attempt is not a fact this system records. A retry
that does not consume an attempt reuses the attempt number, so the artifacts of the run that went
wrong and of the run that replaced it cannot be told apart. "Not the job's current attempt" is the
whole of what is implementable, and the reuse case keeps both runs' artifacts rather than guessing.

The age default for reaping is seven days, and it is a judgement rather than a measurement. Zero would
be defensible on storage grounds and is wrong on every other: an attempt stops being current the
moment the next one is claimed, so a zero-day threshold destroys the working evidence of a failure at
the moment it becomes interesting. A week covers the ordinary case where a failure lands on a Friday
and is picked up the following Friday. Signal payloads default to thirty days because the derived
fields the pipeline actually reasons over survive compaction. Neither default can be set to zero: the
validator requires at least one day, so a missing or mistyped setting cannot become the configuration
that empties a payload the moment it lands.

Compaction is not free, and the price is worth naming because it is the thing the signal window is
really buying. Two worker tools read the raw payload rather than the derived columns: `source_lookup`
looks for a stack trace in `attributes` and `body` before falling back to the signal's `description`
and `error_message`, and `ticket_search` takes its component and label terms out of `attributes`. A
job claimed after its signal's window has expired - a re-triage of an old fault, or a job that sat
unclaimed that long - therefore runs with no source frames and a narrower ticket query. Both degrade
rather than fail, and a job that ran while the payload was still there is unaffected, because its
artifacts are stored separately. This is inherent to compaction: the alternative is keeping the raw
body forever, and the point of the operation is not to. What it means in practice is that
`SignalPayloadRetentionDays` is a choice about how far back a re-triage still gets full context, not
only about disk. The mechanics are in `docs/security-model.md`.

The Worker now drives both operations, one pass every fifteen minutes, and that schedule is as simple
as it looks. There is no cron expression, no maintenance window and no coordination between hosts: a
second Worker would run its own passes on its own clock. That is safe rather than tidy - both
statements are bounded and idempotent, and two concurrent passes contend on row locks and then find
nothing left to do - but it is not a scheduler, and this deployment is a single host by design.

One pass is one bounded run of each operation, not a loop that drains the backlog. The row budget
bounds what a run writes, not what it reads, and the reap reads work proportional to the artifact
table however small the backlog is, so draining would repeat a table-sized read once per budget of
rows against the same database the Worker claims jobs from. The cost of choosing the bounded pass is
that an accumulated backlog clears over days rather than at once, at the budget divided by the
interval; `docs/single-host-production.md` says what that means for the first start after an upgrade.

Retention reports itself through Worker logs and nothing else. It writes no ledger entry and exposes
no counter, so "how much has been reclaimed" is a database question, not an API one. That is
deliberate: the ledger is the audit trail of what the agent decided, and a maintenance pass that
emptied 500 payloads is not one of those decisions.

That is also why a timeline learns about a reaped payload at read time rather than from a stored
marker. Making the fault ledger honest about a missing payload could have been done by appending a
`PayloadReaped` event, and that was rejected: it would put maintenance into the record of what the
agent decided and would duplicate a fact the artifacts table already holds. The `payloadState` on
each event is resolved when the timeline is read, so two reads of the same immutable row can differ
once retention has run in between - which is correct, because what changed is the payload, not the
event. The cost is one indexed existence check per event with a payload reference, over a row set
already bounded by one fault. Compaction is the one place where a stored marker was the right answer
instead, for the reason `infra/postgres/init/028-signal-payload-and-artifact-retention.sql` gives: an
emptied signal payload and one that arrived empty are the same bytes, so absence there proves
nothing. A deleted artifact row is not ambiguous in that way.

## Simple Access Control vs Enterprise RBAC

The current implementation can enable a minimal host-managed API-key boundary. Each accepted key maps
to one tenant and a fixed-window rate-limit partition; any valid key is the minimal action operator for
that tenant. The local walkthrough keeps this boundary disabled and uses demo identity only for local
review. This is not enterprise identity, RBAC, managed key distribution or a secret store. Real auth
providers and finer-grained authorization remain deferred.

## Domain Records with Application-Owned Behavior

Domain types are intentionally simple records and enums in this project's scope, but domain concepts live in the Domain layer so they can be reused across Application workflows without creating Application-to-Application coupling. Workflow behavior, validation policy and partial-failure handling stay in Application services so the public sample remains easy to inspect without DDD ceremony.

## Reference Implementation vs Framework

A reference implementation is easier to build and understand. A framework requires stable APIs, compatibility guarantees and long-term support.

## Internal Dispatcher vs MediatR

A lightweight internal dispatcher keeps this project dependency-light. MediatR v12 can be familiar for many .NET developers, but newer MediatR versions may introduce licensing considerations. This project uses an internal dispatcher/pipeline and can document MediatR as an optional alternative later.

The replacement boundary is the dispatcher engine, not the hosts or use-case contracts. A MediatR swap should replace `ApplicationDispatcher`, `RequestValidationBehavior`, `DispatchLoggingBehavior` and the dispatcher delegate shape with MediatR request handling and pipeline behaviors. The stable contracts are `IApplicationDispatcher`, the request marker interfaces and `IRequestHandler<TRequest, TResponse>` handlers. Hosts should continue depending on `IApplicationDispatcher` so API and Worker composition do not learn which dispatcher engine is active.

That swap would still require deliberate adapter work because the current dispatcher signatures are not MediatR signatures. Keeping the seam at `IApplicationDispatcher` avoids spreading a framework dependency across hosts while preserving a clear migration path if a team prefers MediatR in its own application.

## FluentValidation vs Custom Validators

FluentValidation is used for request-shape validation because it is familiar to many .NET teams, has no MediatR dependency and keeps rule composition separate from handler orchestration. Handlers that need normalized value objects use a neighboring `Normalizer.cs` instead of asking validators to both reject invalid input and build workflow state.

The MediatR decision remains separate. This project still uses its internal dispatcher and pipeline behaviors; FluentValidation provides the rule engine only.

## .NET 10 LTS vs Older Targets

.NET 10 LTS is the preferred baseline for a new project started in 2026. Older .NET versions may be familiar to more teams, but they have shorter remaining support windows.

## `0.x` vs `v1.0.0`

A `0.x` version communicates that the project is useful but evolving. `v1.0.0` should wait until contracts, docs and extension points are stable.

## Raw String Identifiers vs Strongly-Typed Value Objects

Identifiers like `TenantId`, `UserId`, `CorrelationId` are passed as `string` and `Guid` throughout the codebase rather than as strongly-typed value objects (e.g. `readonly record struct TenantId`). Value objects offer compile-time safety against argument-mix-ups and centralized validation, but introduce friction with `System.Text.Json`, `Npgsql` parameter binding, and `IOptions<T>` binding at this project's current scope. The current implementation accepts the small risk of string mix-ups in exchange for transport simplicity. A future scope that grows multi-context handler signatures (tenant + user + correlation + ...) may revisit this.

## Canonical Migration Checksums vs Ledger Rewriting

Migration checksums normalize a decoded leading BOM and CRLF or bare CR line endings before hashing,
so a fresh database gets the same durable identity from Windows and Linux builds. Meaningful text,
script names and script ordering remain significant. The migrator accepts only the deterministic LF
and CRLF legacy hashes attached to the exact released version and migration name.

This leaves historical applied rows untouched and avoids rerunning schema changes merely to adopt a
new checksum representation. The cost is a small closed compatibility policy in the migration catalog.
Unknown hashes and identity changes still stop startup, and operators must restore trusted released
files or ledger state rather than editing a migration or broadening the accepted set.

## Local Tenant Partition And API-Key Mapping

The auth-disabled local path keeps one server-configured incident-data tenant through
`IIncidentTenantContext`. When API-key authentication is enabled, API composition replaces that
request scope with the tenant mapped from the accepted host-managed key. Request bodies, OTLP
attributes and demo headers cannot select the tenant, and demo identity never grants action approval
authority. This is a narrow reference boundary, not a claim of production multi-tenant isolation.

## Sequential Ledger-Backed Governance

Tool policy evaluates `rate_cap`, `precondition` and budget state by reading the append-only ledger. This is simple and inspectable for the MVP because worker delegation is sequential. It is not a parallel-safe counter mechanism; future parallel fan-out would need serialized policy evaluation or atomic counters to avoid two workers passing a cap at the same time.

## Token Budget Overshoot

`MaxTokens` means the backend will not start a new model call once the current-attempt budget is already reached. A single in-flight call can still overshoot the limit because final usage is known only after the provider responds. The overshoot is recorded as a `BudgetEvent` instead of hidden.

## Local-Safe Ceilings Allow Slower Generation

The shipped configuration uses one local-safe profile: 300 seconds per chat-provider HTTP attempt,
600 seconds per investigation attempt, and `MaxOutputTokens: 8000` for both `analysis-chat` and
`report-chat`. Their `ContextWindowTokens` remains 8192, and the orchestrator retains
`MaxTokens: 200000` and `MaxReprompts: 2`.

The 300-second provider deadline accommodates slower local reasoning generation while keeping one
call noticeably below the 600-second investigation budget. That separation leaves time for other
investigation work and keeps a provider-owned generation timeout reachable before the whole attempt
expires. The earlier 2000-token analysis ceiling cut off a local reasoning-model response before it
could complete its final answer. The 8000-token ceiling gives `analysis-chat` room for both reasoning
and the answer; `report-chat` uses the same bound for a consistent shipped profile. On most servers,
reasoning and final-answer tokens share that output allowance.

`ContextWindowTokens: 8192` only bounds the backend's prompt-size estimate. It neither subtracts
from nor reserves room in the separate 8000-token output allowance. Embedding calls keep their
separate 30-second default timeout.

All three values are safety ceilings, not target token consumption or expected latency. A successful
run can finish far below them, and raising an output ceiling does not reserve tokens for the final
answer or require the provider to consume them.

This profile gives slower local models more time and output allowance. Cloud operators can tighten
host timeout and triage route/budget overrides to match their latency and cost requirements.
Until streaming stall detection is implemented, a real stall can take longer to produce a failure.
The investigation's remaining wall-clock budget still cancels an in-flight model call; increasing
the provider timeout does not extend that budget. A provider-owned timeout first consumes the current
job attempt and can retry while attempts remain. A later call canceled by the remaining investigation
wall clock instead dead-letters immediately without consuming another job attempt.

## Provider Retries Prefer Bounded Uncertainty

The OpenAI-compatible generation client retries only HTTP 429/503 responses and failures known to
precede dispatch: name resolution, secure-connection establishment, proxy-tunnel establishment, or
a connection error whose socket cause is connection refused, timed out, host unreachable, network
unreachable, host not found or address not available. It honors a positive `Retry-After` delta or
future date, capped by the configurable `MaxRetryDelaySeconds`, and otherwise uses capped
exponential delay. It does not retry 501/505, a generation timeout, reset, response-ended failure,
generic connection error or other server errors. Redirects are disabled.

Embedding creation keeps a broader retry set than generation because that operation is treated as
idempotent: HTTP 408, 429 and all 5xx responses except 501/505, plus configured timeouts and
transport failures, are retried up to its own limit and its own configurable `MaxRetryDelaySeconds`.
501/505 are the one exclusion, because a request the endpoint will never accept is not made
acceptable by repeating it; the retry predicate and the terminal classification are derived from one
rule so the two can no longer disagree. Once the budget is exhausted, 429 and retryable 5xx responses
and the positively safe pre-dispatch failures above are
`Unavailable`; 408 and a configured timeout are `GenerationTimeout`; 501/505 and configuration
errors are `RejectedRequest`; other exhausted transport failures are `TransportFailure` with
`transport_error`; and invalid JSON or an empty vector is `InvalidResponse`. Caller cancellation
remains distinct. `TransportFailure` consumes the ordinary finite job attempt budget, becoming
`RetryPending` while attempts remain and `DeadLettered` at `MaxAttempts`; it never enters outage
backpressure merely because it is a provider exception.

This is deliberately conservative. A transient HTTP 500 may now dead-letter as
`provider_dispatch_outcome_unknown` even when a later attempt would succeed, because the backend
cannot prove that the provider did not accept and begin the first generation. A timeout consumes the
finite job attempt budget, and output-limit exhaustion, rejected requests and ambiguous interruption
dead-letter immediately. Only a failure classified as `Unavailable` enters the delayed
no-attempt-cost path and process-local backpressure. The policy avoids unbounded replay of work that
may already have been dispatched, and it is not a distributed provider-health mechanism.

A route may now name a fallback route, and a call the provider answers with `Unavailable` or
`GenerationTimeout` is retried once there. That is the whole of it, and the boundaries above are why
the set is that small: the kinds left out are either the model's own answer being wrong, where a
second provider is the same money spent twice, or a dispatch whose outcome is not known, where a
second call is the opposite of the stance the previous paragraph takes. One hop, one shared deadline,
both calls charged, and the primary's kind is still what the job runner classifies. A fallback that
answers does not clear claim backpressure, because it says nothing about the provider that failed.
See `docs/model-gateway.md`, "Route Fallback".

Failed generations can return billable usage. When they do, failure accounting writes a failed
`ModelCall` and its `BudgetEvent` charge in the same transaction as the fenced job/fault disposition.
The shared call id in `payload_ref` deduplicates a replay of failure persistence under the job lock.
An explicit provider total of zero is preserved as a zero charge; absent usage remains unknown and
is not estimated. If ownership is stale, accounting stays visible while the fenced job and fault
mutations do not apply.

That boundary favors audit honesty over a guessed cost: unknown failed usage can leave the ledger and
cost rollup below the provider's eventual invoice. Success accounting still estimates missing or
incomplete usage. When both ledger rows exist, model-call accounting is atomic as a pair, but it is
not an exactly-once distributed billing system beyond the database lock and call-id deduplication
boundary. Streaming idle detection and progress recovery remain separate design work.

A call that fails over is charged twice, once per provider call, and that is the intended answer
rather than an oversight: both calls happened, and a provider invoices for a generation it failed
partway through the same as for one that succeeded. The failed call's `ModelCall` row and its
`BudgetEvent` charge are made durable before the second call is allowed to start, so the attempt is
never left having spent tokens the ledger cannot account for; if that write fails, no fail-over is
attempted and the original failure carries its accounting to the attempt-failure path unchanged. The
cost of that ordering is one extra ledger round trip on a path that is already recovering from a
provider failure.

## Budget And Governance Exhaustion Dead-Letters Instead Of Retrying

Reaching a bounded-run limit is treated as a permanent outcome for the job, not a transient fault. When an attempt hits the token budget, the wall-clock budget, the route context window, the per-attempt worker budget (`MaxWorkers`), a bounded turn limit (the configured orchestrator `MaxTurns` plus its reprompt allowance, or the worker turn allowance) or the orchestrator's reprompt allowance itself (`MaxReprompts`, as `triage_budget_orchestrator_reprompt_limit_reached`), when backend governance denies a worker tool call, or when the rehydrated configuration names an orchestrator route it does not contain, the job is dead-lettered immediately with its own `last_error_code` and no next attempt time. It does not spend the remaining `MaxAttempts`.

The reason is that a replay reads the same configuration snapshot and the same policy rules, so it would exhaust or be denied in the same place while spending another full budget of provider tokens. Retrying would multiply cost and delay the operator signal without changing the outcome. The distinct codes (`triage_budget_*` and `triage_governance_*`) are what an operator greps to tell an under-provisioned budget apart from an ordinary fault; the durable `BudgetEvent` and policy-decision ledger rows written before the failure are unchanged, so a dead-lettered exhaustion is exactly as audit-visible as the retried failure was.

Inside a worker the same line is drawn explicitly rather than by where a throw happens to sit. Only
the worker output failing schema validation is repromptable, because the model can correct its own
JSON on the next bounded turn. Each worker correction is a `Warning` with job, attempt, role and
counter data and writes a bounded `BudgetEvent`; orchestrator corrections have the same durable
ledger visibility and a specific closed reason. A budget stop or a governance denial raised during a
worker turn leaves the worker loop instead of being spent as a reprompt, so a denied tool call is
never retried by reprompting the model.

The output validator returns at most 20 violations from one response, so a correction turn can ask the
model to repair all known issues at once. That correction prompt includes the role's output schema, but
neither the schema nor the prompt is logged or written to the ledger. Diagnostics use fixed validator
wording and application-selected paths into the configured schema. An unexpected, model-controlled
property contributes only a count, not its name. Model output and raw `JsonException` text or paths are
excluded. Property names are not treated as universally safe merely because they appeared in JSON.

Exactly one matching outer Markdown fence around otherwise bare JSON is tolerated at the worker-output
and orchestrator tool-argument boundaries as a recovery aid. The opener may be bare or carry the
`json` language tag, and must match either a closing triple backtick or triple tilde fence. Mixed
prose, nested, multiple or mismatched fences, and fenced or otherwise string-encoded `report_json`
content remain invalid. The published role instructions still require bare JSON. Schemas intentionally
remain union-free: optional fields whose value would be
`null` must be omitted, rather than using a `type` union with `null`. This keeps the shipped schemas
and instructions aligned without broadening the validator's accepted schema dialect.

When the worker has spent its correction allowance, it dead-letters immediately as
`worker_output_invalid`. Its durable reason is a fixed bounded classification with no response or
validator-exception content, rather than a retryable generic exception. The strict report envelope is
separate work; this tolerance does not relax it.

The orchestrator half of the same mechanism is treated identically. When the orchestrator has spent
its `MaxReprompts` allowance - on an absent tool call, an unsupported tool, invalid `delegate`
arguments or an invalid `publish_report` - the attempt dead-letters as
`triage_budget_orchestrator_reprompt_limit_reached`. One code covers all four causes because the
permanent condition is one condition, the allowance being spent, and that is the knob an operator
would change; which turn could not be corrected stays visible per reprompt in log event 3401 and its
`orchestrator_reprompt:` ledger `BudgetEvent`, and for the uncorrectable turn itself in log event
3403.

The trade-off is honest: an attempt that failed only because a transient slowdown consumed its wall clock is also dead-lettered rather than retried. That is deliberate for a reference deployment, where a visible dead-letter with a specific code is more useful than a silent retry loop, but it means budgets must be provisioned for the slowest acceptable run. Provider outages are classified first and keep their separate delayed-retry path, so an outage never reaches this classification.

## File-Backed Memory Is The Write Path

Memory content stays in reviewed files instead of an unauthenticated admin endpoint. Source path is
the stable database identity; a content change updates and re-embeds that item, and a removed file is
deactivated from retrieval. This keeps provenance simple and prevents an edited file from leaving a
second stale live item. Runtime resync is opt-in and bounded; operators may enable it for reviewed file changes without adding a memory write API. The default remains startup-only synchronization.

Whole-corpus re-embedding stays on the same side of that line. It is a console command on the
existing hosts, `memory rebuild`, rather than an HTTP endpoint, because it is a host-wide
maintenance action with no tenant-scoped caller behind it and a write API for it would need an
administrative identity this system does not have.

## Documentation Fit Is Evidence Classification

`CurrentReleases` is a manually maintained per-service marker in the snapshotted triage configuration.
Retrieved memory is labeled from its service and release metadata before report publication. The backend
can show current, stale-only, mixed historical, missing and multiple-current-document states, but it
cannot prove that two documents agree semantically or that a runbook is operationally correct. Multiple
current matches therefore add an explicit review limitation rather than being silently resolved by the
model.

## Memory Embedding Model Changes Require Re-Embedding

Memory retrieval filters by tenant, embedding provider, embedding model and embedding dimensions.
That avoids mixing incompatible corpora, and it also means changing the embedding provider or model
makes an existing corpus unretrievable until it is re-embedded. Re-embedding is not automatic: every
reviewed file has to go back through the provider, which is an operator's decision about cost and
timing rather than something a restart should take on its own.

What changed is that the corpus no longer goes quiet about it. Each published generation records the
route that built it, a synchronization pass compares that against the configured route before it
requests a single embedding, and a mismatch leaves the previous corpus current and reports
`memory_embedding_route_changed` through the memory-sync health status and
`GET /api/v1/health/memory-corpus`. `memory rebuild` on either host performs the re-embedding and
publishes one new generation transactionally.

Two limits are worth stating. The pre-check sees only what configuration declares, a provider
identifier and a model name, so a provider that serves different weights under an unchanged model
name is not detected until something is re-embedded and the returned vector shape is compared. And a
corpus seeded before generations recorded a provider identifier is reported as `Unrecorded` rather
than assigned one, because attributing it to the currently configured provider would assert
something nobody observed; a rebuild records it.

Generation identity is owner-scoped, and retrieval is tenant-scoped. One current generation per seed
owner is a database invariant, and the reconciliation transaction refuses to publish a corpus that
would leave its own owner holding two vector spaces. It does not arbitrate between owners: two seed
owners in one tenant that use different providers with the same adapter name, model name and vector
width would both be searched. The shipped configuration has one owner per tenant, and separating
owners further would mean scoping retrieval by owner, which is a change to what a tenant's memory
means rather than a fix to this check.

## Bounded Memory Reranking Instead of Database Full-Text Search

Memory search overfetches at most four times the configured result count, capped at 100 candidates,
then reranks in Application with deterministic lexical and trusted metadata features. This recovers
relevant chunks that vector-only `TopK` can hide while keeping tenant, embedding-route, dimension and
active-item isolation inside the PostgreSQL query. Current same-service documentation has priority;
stale-only evidence remains eligible and keeps its stale label. Component and evidence-kind boosts use
only exact normalized query matches against stored metadata and code-owned aliases.

This is a bounded reference implementation, not a general hybrid-search engine. Its fixed lexical rules
may need revision for multilingual or much larger corpora, and overfetch adds query and application work.
PostgreSQL full-text search, reciprocal-rank fusion, adaptive retries and caller-configurable ranking
weights remain deferred. Each execution makes exactly one embedding request and one repository search.

## Deterministic Grouping Is Not Incident Correlation

Delivery deduplication, open-fault grouping, suppression and recurrence are deliberately separate.
A duplicate delivery is ignored after its first accepted signal. A distinct matching delivery can attach
to one open fault, be stored as suppressed against a recently closed fault, or advance a recurrence state
once the silence window has elapsed. The state is keyed by the effective fingerprint-rule generation and
uses a PostgreSQL upsert, so concurrent accepted recurrence deliveries count once each and create at most
one threshold-crossing escalation intent. This keeps the behavior auditable, but the selected fingerprint
and suppression policy can still be wrong for the operator's real incident boundary. Cross-fault incident
correlation remains a later capability rather than an implicit effect of grouping.

## Re-triage Reuses Untrusted History

Recurrence escalation is deterministic database state, but the prior report copied into a new investigation is model output and incident-derived context, not authority. The prompt labels it as an untrusted hypothesis; the worker must independently ground its result and cite the new `RecurrenceState` artifact before publishing a successor. This prevents historical text from becoming sticky fact, but it does not make model reasoning a security boundary. The current release has no manual re-triage endpoint or mass-issue-flip trigger; the latter remains an explicit scope cut.

## Provider Backpressure Is Process-Local

Provider-outage backpressure is deliberately held in each Worker process. It prevents a local outage from rapidly consuming retries and clears after a successful model call, but multiple Worker hosts do not share breaker state. A future distributed deployment needs coordinated provider health if a global circuit is required; the current release remains a local/reference deployment and does not claim that property.

## Grounded Evidence vs Correct Conclusions

Report grounding proves that each persisted evidence row came from a citable artifact visible to the job and that any stored quote was an exact substring of the redacted artifact payload. It does not prove the model's classification is correct. This is an intentional MVP boundary: durable evidence makes review possible, while evaluation of reasoning quality remains outside the backend transaction.

## Renewable Worker Leases Require Cooperative Calls

The Worker renews an owned lease at roughly one third of its duration while processing an investigation.
Renewal and terminal job updates are fenced by job, attempt, worker and an unexpired lease, so a stale
owner cannot publish a report or overwrite the current owner. When renewal fails, ownership is lost or
host shutdown begins, the Worker cancels the in-flight investigation and observes the renewal loop before
releasing its slot. A job left in `Processing` becomes claimable after its current lease expires.

This protects the durable ownership boundary, but it cannot forcibly interrupt a provider or tool that
ignores its cancellation token. The shipped model and tool paths propagate cancellation; custom adapters
must do the same to avoid work that can no longer publish a result.

## One Live Tool Policy Path

`ToolRuleEngine` is the single tool-policy mechanism. Immediate Worker reads feed it role grants;
backend-owned post-report proposals feed it the exact snapshotted `Actions.AllowedTools` grant.
External proposal facts use a transaction-bound ledger reader only after fault-first current-origin
and job locking; this preserves one evaluator while serializing preconditions and accepted-use caps.
Capability registration prevents configuration from turning a read into an external action, and
external actions never enter the investigation model surface. The earlier standalone executor and
audit repository were removed instead of retaining a parallel policy interpretation. The released
`infra/postgres/init/006-tool-audit.sql` migration keeps its unused legacy table rather than being
dropped or rewritten, so fresh and upgraded databases agree on the migration catalog. Its leading
comment was edited once before 1.0, when internal tracker identifiers and roadmap labels were removed
from the comments of six scripts (`004`, `006`, `007`, `008`, `009`, `010`). All six belong to catalog
migration version 1, which the ledger records under a single checksum, so that edit changed that one
checksum on purpose, and it was safe only because no durable database existed yet.
`docs/versioning.md` records why, and the freeze holds from 0.4.0 forward.

## Read-Only Cost Rollup Uses Operator-Maintained Pricing

The model-cost surface aggregates the existing durable `ModelCall` ledger metadata by tenant and UTC
hour. It deliberately does not estimate cost in the write path or retain the earlier per-call
estimator/repository as a second accounting interpretation. This keeps ModelCall and BudgetEvent
writes unchanged and makes malformed history visible as unpriced instead of failing a write or
silently producing zero cost.

The cost is operational simplicity: pricing rows are effective-dated operator-maintained database
configuration. The API cannot add or reload prices, convert currencies, emit alerts or enforce quotas.
A call is priced only with exactly one case-sensitive interval match; missing, overlapping or tied
history remains unpriced. These limits are preferable to granting a new mutation or notification
authority before the read model is proven.

## Hand-Edited Prices Are Constrained, Not Replaced By An API

Prices are host-global and every API identity in this system is tenant-scoped, so a price endpoint
would need a cross-tenant admin identity that does not exist here and that nothing else needs. The
choice was therefore between leaving the table unguarded and constraining the hand-edit, not between
a prompt and an API.

Schema version 21 constrains it: a write must name an author in `administered_by`, the change time is
stamped by the database rather than accepted from the statement, an interval that overlaps an
existing one for the same provider and model is refused, and `DELETE` is refused in favour of setting
`effective_to_utc`.

The costs are real and are accepted. The author is unverified free text, because a column asserting
an authenticated identity where none exists would be worse than an honest label. Attribution is
last-writer-only rather than a change history, so two successive corrections leave only the second
one's name; the runbook works around that by prescribing interval closure over in-place editing, and
a full audit table was judged more machinery than the problem warrants. In-place correction still
rewrites what an already-closed hour costs, which is deliberate - the alternative is being unable to
fix a wrong price at all - and the runbook says so plainly. An upgrade over a database that already
holds overlapping intervals fails and names the conflicting pairs rather than closing one, because
choosing between two prices is the arbitration the read path deliberately refuses.

## Cost Alerting Belongs To The Operator's Own Tooling

A threshold evaluator over the cost rollup is not built, and the reasons are structural rather than
schedule-driven: the producer of a breach is a background pass with no tenant while every rollup read
is tenant-scoped, there is no cross-tenant operator principal to address an alert to, an
acknowledgement would gate nothing and so would devalue the approval vocabulary it borrowed, and the
one delivery path in the repository requires an origin report by foreign key that a threshold breach
does not have. The full reasoning is in `docs/cost-tracking.md`.

The cost is that this system raises no alarm about its own spend. That is the right side of the
boundary: an alerting rule over the existing authenticated cost endpoint keeps its own thresholds,
history, routing, deduplication and silencing, which an in-process evaluator here would do worse, and
this project is already a consumer of an observability pipeline rather than an observability backend.

## Durable Evaluation Queue Is Not A Second Action Outbox

Report publication and evaluation cannot share one long transaction across arbitrary workflow code.
IncidentCompass instead commits a minimal immutable intent with the report, then evaluates it through
a separate bounded Worker pump. The queue uses database-clock renewable leases, random fences,
bounded deterministic retries and a maximum attempt count. A crash or lost lease can therefore replay
evaluation, so workflows must be deterministic and proposal creation uses a stable proposal key plus
the existing proposal transaction's idempotency boundary.

The queue stops at proposal creation. It has no adapter port, approval decision, dispatch state or
new ledger vocabulary; `action_approvals` remains the only approval and external-dispatch outbox.
This adds durable scheduling and recovery without creating a competing policy system. Shared
composition registers only non-secret Telegram, ticket-create and ticket-update descriptors for
configuration validation, while Worker composition registers their workflows and adapters.
Deterministic Docker tests exercise handoff and recovery cases without calling a real provider.

## At-Most-Once Action Dispatch Prefers Visible Uncertainty

The post-report action path separates immutable proposal/approval from a bounded Worker dispatcher.
The dispatcher locks the fault before the action, rechecks current report and policy, freezes one
owner/fence/deadline claim and calls the exact registered adapter with the stored bytes and action id.
It never automatically invokes that action again after claim. Dry-run terminates without a call, and
binding or policy drift fails closed.

This is deliberately at-most-once backend invocation, not exactly-once delivery. If a provider accepts
the request but the response or terminal database commit is lost, IncidentCompass cannot prove the
external outcome. Recovery records `dispatch_outcome_unknown` after the database deadline and fences
late completion instead of risking a duplicate side effect. A later adapter may use the action id as
its own idempotency key, but IncidentCompass does not rely on provider idempotency for correctness.
Telegram is the first production side-effect adapter. Its fixed-recipient design intentionally trades
flexibility for a smaller authority surface: public routes select only one Worker-owned binding and
the backend generates the message. A fault-locked 30-minute cooldown after confirmed live success or
outcome-unknown reduces duplicate alerts, but it can suppress a legitimate rapid follow-up. Ticket
create is the second side-effect adapter. It trades general provider selection and exactly-once
delivery for one Worker-owned GitHub repository, mandatory approval, deterministic marker lookup and
at most one POST after bounded preflight. A prior outcome-unknown is never automatically reconciled
with another write, so an operator may need to inspect the provider. Ticket update is a distinct
side-effect adapter limited to a bounded evidence comment on one exact cited issue. It uses mandatory
approval, a deterministic comment marker, bounded target/comment preflight and one possible POST;
this smaller surface excludes title, state, label, assignee and repository mutation. General-purpose
model-selected external actions remain separate work.

## Compact External Projection, Not General Reconstruction

Confirmed live Telegram and GitHub actions store one indexed immutable projection beside the action:
external kind/id plus a closed before/after marker. This makes tenant-scoped operator correlation cheap
without indexing raw provider JSON or copying credentials, routes, response bodies, report text or
prompts into a second audit system. The detailed bounded canonical result remains in `ActionResult`.

The trade-off is intentionally narrow reconstruction. Failure, dry-run and outcome-unknown rows have
no external-success projection, and the projection does not model arbitrary provider state or later
out-of-band changes. Cross-system reconciliation remains future work rather than being inferred from
uncertain provider outcomes. Payload retention deliberately leaves both halves alone: the projection
lives on the action row, and `ActionResult` artifacts are excluded from reaping by kind because they
are the audit record of an external effect rather than working evidence. That is not only a matter of
current predicates - the approvals table refuses DELETE outright and refuses any UPDATE of the
projection columns outside the transition that first sets them, so a future retention predicate that
tried to reach the projection would abort rather than succeed quietly.

The projection is now readable in two directions rather than one: by exact resource pair, and by
fault. A general correlation capability was considered and not built. Widening the resource lookup to
prefixes or fuzzy matching would turn an exact audit question into a guess over identifiers this
system does not own, and a query surface that composed arbitrary predicates across the ledger, the
artifacts and the approvals would be a reporting engine whose cost and blast radius nobody here has
measured. What shipped instead is one specific bounded second lookup - the inverse of the one that
already existed - plus `faultId` on the list item so the two compose into a pivot. Two directions
over a closed vocabulary is a claim that can be tested; "correlation" in general is not.

Its index is worth naming as a weaker claim than its neighbours make.
`ix_action_approvals_tenant_fault` ships on the shape of the predicate rather than on a measured plan
over a large corpus, unlike the retention indexes in
`infra/postgres/init/028-signal-payload-and-artifact-retention.sql`, whose comments carry real
`EXPLAIN` numbers. What is verified is only that the planner chooses it for the query as written.

## Two Compose Naming Styles Are Kept

The root file is `docker-compose.yml` while its siblings are `compose.mock.yml`,
`compose.production.yml` and `compose.evaluation.yml`. Both names are valid to Docker Compose, and
the split is an accident of when each file was added rather than a distinction between them.

Renaming the odd one out was considered and rejected. `docker-compose.yml` is a name Compose loads by
default, so the quickstart, demo and runbook commands reach it two ways: the layered ones name it
explicitly with `-f`, and the bare ones rely on the default. A rename would have to update both
kinds of command across the documentation and the scripts, to buy consistency in a file listing and
nothing at runtime. The inconsistency is cosmetic and stays.

## An Untested Diff Is Approvable, Loudly

The backlog item this approval step comes from asks that an untested artifact create no approvable
proposal. That criterion was written on the assumption that something would run a test. Nothing in
this release does: no process is started anywhere in the product, and the architecture test that
fails the build when process I/O appears in the Application project is unchanged. Read literally, the
criterion would mean the feature ships with no reachable path at all, since every diff carries
`not_executed`.

What shipped instead is the honest equivalent with test execution deferred. A proposal is created,
and the frozen payload states in three places that no test ran: `testOutcome` is the literal
`not_executed`, `testCommandId` is `null`, and `testStatement` is a sentence ending "Approving it
approves an untested change." All three are inside the bytes the approval hash covers, and the review
summary a person sees in the approval list begins with `UNTESTED CHANGE`. A human who approves one of
these is knowingly approving an unverified change; what the criterion forbids, and what does not
happen, is that this occurs silently.

The part of the criterion that is enforced exactly is the other half. A diff row whose `test_outcome`
says anything other than `not_executed`, or that names a test command, is refused with
`remediation_diff_unsupported` and creates nothing, and a payload whose test fields say anything else
cannot be read back at all. So the day a release runs a test, it cannot reuse this payload shape by
accident: the artifact that ran one and the artifact that did not can never be confused, and saying
that a test passed will take a deliberate change to the payload contract, the same way it already
takes a deliberate migration to relax the table's own checks.

## A Push Is Byte-Equivalent Over A Proved Intersection, Not Over Everything

The backlog item behind the governed branch push asks that the pushed tree be the approved base with
the approved diff applied. Taken literally over what this product calls a base, that criterion cannot
be met, and meeting it would be worse than not meeting it.

A base tree identity is a SHA-256 over the *admitted* files of a monitored checkout. Admission has no
notion of what a repository tracks: there is no `.gitignore` handling anywhere in the product, and the
checkout's own `.git` directory is deliberately skipped, so the admitted set includes build output,
local logs and any environment file sitting in the working tree. Building the pushed tree from that
set would publish all of it, and would give the commit no defensible parent, since a tree made of
admitted files is not a descendant of anything on the remote.

What ships instead is the honest restatement, and it is what a reviewer is told:

> The pushed commit's tree equals the remote base commit's tree with exactly the files the approved
> diff writes replaced by the bytes that diff produces when applied to its approved base. Before the
> push, every path the approved base and the remote base commit have in common is proved
> byte-identical, and any path present on only one side is enumerated: a path only the local base
> holds is untracked by the remote and is excluded from the push, and a path only the remote holds
> refuses. Divergence at any compared path, a truncated remote listing, or a tree entry this product
> cannot reproduce refuses and requires a fresh proposal.

Three things follow from that wording and are worth stating plainly.

**The excluded set is named, not ignored.** The comparison returns the local-only paths in order, the
count is frozen into the approval payload and shown in the review summary, and the digest the approval
freezes is computed over those paths as well as over the proved ones. So "byte-equivalent over the
intersection" is a claim with a stated boundary rather than a claim with a silent one. Those files are
also unreachable rather than merely unwanted: only the paths the diff names are ever read back out of
the patched copy, and a diff may name at most sixteen policy-checked paths.

**A path only the remote holds refuses, and that is deliberate.** It is either a file the checkout
deleted - in which case the pushed commit would silently keep it, because the base tree does - or a
file admission could not see, such as a symlink or a submodule, in which case the pushed tree carries
content the approved base never described. Accepting the intersection as whatever the local filesystem
happened to expose would make the proof a function of what was hidden from it. A clean checkout of the
base branch has no such path, so the strict answer costs nothing in the ordinary case.

**The resulting commit is bound by determination rather than by name.** Every input to the commit is
in the frozen payload and hashed: the parent, the base tree, the diff, the pinned author and committer
instant, and a backend-composed message carrying no model text. Those bytes determine exactly one
content-addressed commit. Naming that commit id in the payload would mean either implementing git's
tree and commit object formats inside this product - a second implementation of a format whose failure
mode is a feature that refuses forever - or creating objects in the remote repository while the
proposal is still waiting for a person, which is an external write before an approval. Neither is
worth the literal value. The dispatch proves the commit instead: it refuses unless the commit the
provider built has exactly the approved parent and the tree this dispatch created, before the
reference is touched, and the commit id is recorded on the action row once it exists.

## One Credential For Issues And For Code

The code repository binding is the issue repository binding: the same owner, the same repository and
the same token that `IncidentCompass:Tickets:GitHub` already carries. Only the base branch is new.

The alternative was a second repository setting and a second credential, which would allow a
deployment where incidents are filed in one repository and fixes pushed to another. That flexibility
is real and it is not free: it doubles the secret inventory, and it creates a path where a credential
with write access to source is configured beside the one an operator thought they were configuring,
with nothing forcing them to notice. Reusing one binding means the repository that receives a branch is
the repository an operator already named and reviewed.

What does change is the scope that one token needs: creating branches needs write access to repository
contents and opening a pull request needs pull-request write, neither of which filing issues does. That
widening is deliberate rather than incidental. It is made by an operator who is turning `branch_push`
or `pr_create` on, both of which are off in the shipped configuration and require editing the tool's
mode and `Actions.AllowedTools`, and the Worker refuses to start if either is enabled without a
repository, a credential and a base branch configured. A deployment that genuinely needs to separate
the two repositories is a change with its own review, not a default.

## A Pull-Request Description Cites Only Values Of Fixed Shape

The backlog item asks that a pull-request description cite the originating report, the GitHub issue,
test evidence and the uncertainty "without exposing credentials, absolute host paths, source bodies or
prompts". Two of those four needed a decision rather than a filter.

**There is no test evidence, so the description says so.** This release starts no process and runs no
test command; the code-write payload refuses any diff whose test outcome is anything but
`not_executed`. Citing test evidence therefore means citing its absence, in the same words the payload
already uses, plus the sentence that a reader should not merge on the strength of the description.

**The service name and the release are left out, even though the approval carries them.** They are the
only values on this path an ingested signal can influence, and a pull-request description is a public
page rendered by someone else's markdown. Rather than deciding how to escape a name for a renderer this
product does not control, the composer simply has no free-text parameter: everything the body prints is
a report identifier, an issue number, one of three confidence words, a commit name, a count or a hex
digest. The service and the release stay in the reviewer's summary, which only an operator reads. The
cost is a description a reader cannot skim for "which service" without opening the report it names, and
that is the right side of the trade for a page strangers can read.

The uncertainty it does cite is the report's own recorded confidence - one of `Low`, `Medium`, `High` -
together with the statement that the change was written by a language model and that byte-equivalence
was proved only over the intersection described above. A confidence outside that vocabulary is not
published: the proposal refuses with `pr_create_origin_report_unreadable` rather than composing a
description that omits the one thing it exists to say.

## The Ticket Backlink Is A Second Tool Id, Not A Second Ticket Update

The item asks that a confirmed pull request make "only the governed ticket-comment workflow" eligible
for a backlink. It does, and it does so under a second tool id rather than by reusing `ticket_update`.

The reason is a pair of unique keys. The post-report queue holds one intent per tenant, report and
tool; the approval table holds one proposal per tenant, report, tool and key. A report therefore has
exactly one `ticket_update` for its whole life, and that one is proposed when the report is published -
long before any pull request exists - so its frozen payload cannot learn a number nobody knew when a
person approved it.

The alternative was to defer the ticket comment until the remediation chain settled. That is worse in a
way that matters: the chain passes through three separate human approvals, any of which may simply
never be granted, and a report with no remediation at all would then never receive the comment it
receives today. Deferring would have traded a feature nobody has for behaviour people already rely on.

A second id costs one more configuration entry, one more descriptor and one more workflow, and it
reuses the existing comment's marker, preflight, bound and delivery path rather than duplicating them.
It also keeps the refusal that made the whole question interesting: the comment preflight still declines
any target the provider reports as a pull request, so a governed comment never lands inside a
conversation this product opened. The link goes on the issue and points at the pull request.

The shared payload contract did have to change, and the change is stated rather than absorbed: it is
now six properties at schema version 2 rather than five at version 1, the sixth being the pull-request
number, null for the evidence comment and a number for the backlink. A comment proposed under version 1
and still awaiting approval when a host upgrades is no longer executable and fails closed; the remedy
is a fresh proposal. The `branch_push` payload's own schema version moved to 2 for the same kind of
reason: the frozen sentence a reviewer approves now has to say that executing a push schedules a
pull-request proposal, and a statement inside an approval hash is not a comment.
