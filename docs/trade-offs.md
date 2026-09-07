# Trade-offs

This document records intentional choices and their costs.

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

## Pseudonymization Salt Rotation

User identifiers can be replaced with stable HMAC-SHA256 pseudonyms so later blast-radius logic can
count distinct users without storing raw identifiers. The host-only salt is intentionally outside the
snapshotted triage config. Rotating it breaks continuity with older pseudonyms; running without it
fails safe to redaction and therefore loses distinct-user counting.

## Simple Access Control vs Enterprise RBAC

The current implementation can enable a minimal host-managed API-key boundary. Each accepted key maps
to one tenant and a fixed-window rate-limit partition; any valid key is the minimal action operator for
that tenant. The local walkthrough keeps this boundary disabled and uses demo identity only for local
review. This is not enterprise identity, RBAC, managed key distribution or a secret store. Real auth
providers and finer-grained authorization remain deferred.

## Domain Records with Application-Owned Behavior

Domain types are intentionally simple records and enums in the starter-kit scope, but domain concepts live in the Domain layer so they can be reused across Application workflows without creating Application-to-Application coupling. Workflow behavior, validation policy and partial-failure handling stay in Application services so the public sample remains easy to inspect without DDD ceremony.

## Starter Kit vs Framework

A starter kit is easier to build and understand. A framework requires stable APIs, compatibility guarantees and long-term support.

## Internal Dispatcher vs MediatR

A lightweight internal dispatcher keeps the starter kit dependency-light. MediatR v12 can be familiar for many .NET developers, but newer MediatR versions may introduce licensing considerations. This project uses an internal dispatcher/pipeline and can document MediatR as an optional alternative later.

The replacement boundary is the dispatcher engine, not the hosts or use-case contracts. A MediatR swap should replace `ApplicationDispatcher`, `RequestValidationBehavior`, `DispatchLoggingBehavior` and the dispatcher delegate shape with MediatR request handling and pipeline behaviors. The stable contracts are `IApplicationDispatcher`, the request marker interfaces and `IRequestHandler<TRequest, TResponse>` handlers. Hosts should continue depending on `IApplicationDispatcher` so API and Worker composition do not learn which dispatcher engine is active.

That swap would still require deliberate adapter work because the current dispatcher signatures are not MediatR signatures. Keeping the seam at `IApplicationDispatcher` avoids spreading a framework dependency across hosts while preserving a clear migration path if a team prefers MediatR in its own application.

## FluentValidation vs Custom Validators

FluentValidation is used for request-shape validation because it is familiar to many .NET teams, has no MediatR dependency and keeps rule composition separate from handler orchestration. Handlers that need normalized value objects use a neighboring `Normalizer.cs` instead of asking validators to both reject invalid input and build workflow state.

The MediatR decision remains separate. This project still uses its internal dispatcher and pipeline behaviors; FluentValidation provides the rule engine only.

## .NET 10 LTS vs Older Targets

.NET 10 LTS is the preferred baseline for a new project started in 2026. Older .NET versions may be familiar to more teams, but they have shorter remaining support windows.

## `v0.1.0` vs `v1.0.0`

`v0.1.0` communicates that the project is useful but evolving. `v1.0.0` should wait until contracts, docs and extension points are stable.

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

## Budget And Governance Exhaustion Dead-Letters Instead Of Retrying

Reaching a bounded-run limit is treated as a permanent outcome for the job, not a transient fault. When an attempt hits the token budget, the wall-clock budget, the route context window, the per-attempt worker budget (`MaxWorkers`) or a bounded turn limit (the configured orchestrator `MaxTurns` plus its reprompt allowance, or the worker turn allowance), when backend governance denies a worker tool call, or when the rehydrated configuration names an orchestrator route it does not contain, the job is dead-lettered immediately with its own `last_error_code` and no next attempt time. It does not spend the remaining `MaxAttempts`.

The reason is that a replay reads the same configuration snapshot and the same policy rules, so it would exhaust or be denied in the same place while spending another full budget of provider tokens. Retrying would multiply cost and delay the operator signal without changing the outcome. The distinct codes (`triage_budget_*` and `triage_governance_*`) are what an operator greps to tell an under-provisioned budget apart from an ordinary fault; the durable `BudgetEvent` and policy-decision ledger rows written before the failure are unchanged, so a dead-lettered exhaustion is exactly as audit-visible as the retried failure was.

Inside a worker the same line is drawn explicitly rather than by where a throw happens to sit. Only the worker output failing schema validation is repromptable, because the model can correct its own JSON on the next bounded turn. A budget stop or a governance denial raised during a worker turn leaves the worker loop instead of being spent as a reprompt, so a denied tool call is never retried by reprompting the model.

The trade-off is honest: an attempt that failed only because a transient slowdown consumed its wall clock is also dead-lettered rather than retried. That is deliberate for a reference deployment, where a visible dead-letter with a specific code is more useful than a silent retry loop, but it means budgets must be provisioned for the slowest acceptable run. Provider outages are classified first and keep their separate delayed-retry path, so an outage never reaches this classification.

## File-Backed Memory Is The Write Path

Memory content stays in reviewed files instead of an unauthenticated admin endpoint. Source path is
the stable database identity; a content change updates and re-embeds that item, and a removed file is
deactivated from retrieval. This keeps provenance simple and prevents an edited file from leaving a
second stale live item. Runtime resync is opt-in and bounded; operators may enable it for reviewed file changes without adding a memory write API. The default remains startup-only synchronization.

## Documentation Fit Is Evidence Classification

`CurrentReleases` is a manually maintained per-service marker in the snapshotted triage configuration.
Retrieved memory is labeled from its service and release metadata before report publication. The backend
can show current, stale-only, mixed historical, missing and multiple-current-document states, but it
cannot prove that two documents agree semantically or that a runbook is operationally correct. Multiple
current matches therefore add an explicit review limitation rather than being silently resolved by the
model.
## Memory Embedding Model Changes Require Re-Embedding

Memory retrieval filters by tenant, embedding provider, embedding model and embedding dimensions. This avoids mixing incompatible corpora, but it also means changing the embedding provider or model makes existing memory chunks silently unretrievable until they are re-embedded. Changing the configured embedding provider or model should be paired with a full memory re-seed or migration.

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
`infra/postgres/init/006-tool-audit.sql` migration stays byte-identical and its legacy table remains
unused so fresh and upgraded databases preserve migration integrity.

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
out-of-band changes. Retention, reaping and cross-system reconciliation remain future work rather than
being inferred from uncertain provider outcomes.
