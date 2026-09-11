# Observability

Production-minded model-backed systems need visibility into model calls, latency, token usage, budget decisions and failures without storing sensitive prompt material.

## Minimum Signals

- structured application logs;
- correlation ID;
- request duration;
- model latency;
- token usage;
- budget ledger entries;
- error logs.

## Application Log Event Ids

Every source-generated `[LoggerMessage]` event in the solution declares an explicit `EventId`, so a
log event can be filtered, alerted on and documented without depending on generator-derived
numbering. Ids are allocated in disjoint ranges per area, and a hundred-block per type inside a
range:

| Range | Area |
| --- | --- |
| 1000-1999 | `IncidentCompass.Worker` host |
| 2000-2999 | `IncidentCompass.Infrastructure` adapters |
| 3000-3999 | `IncidentCompass.Application` use cases and governance |
| 4000-4999 | `IncidentCompass.Api` host |

An id is stable once published. A retired event keeps its id reserved rather than recycling it.

### Worker host (1000-1999)

| Id | Level | Meaning |
| --- | --- | --- |
| 1001 | Information | Worker started with its concurrency limit and startup health status. |
| 1002 | Warning | Worker startup health check failed; polling continues. |
| 1003 | Warning | Worker polling failed; polling continues after backoff. |
| 1101 | Warning | Triage job lease ownership was lost; in-flight work is cancelled. |
| 1102 | Warning | Triage job lease renewal failed; in-flight work is cancelled. |
| 1201 | Warning | Approved action polling failed; polling continues after backoff. |
| 1301 | Warning | Post-report action evaluation polling failed; polling continues after backoff. |
| 1401 | Warning | Claimed triage job processing failed after claim. |
| 1402 | Warning | Claimed triage job processing failed while draining the worker. |
| 1501 | Warning | Approved action dispatch failed after claim. |
| 1502 | Warning | Approved action dispatch failed while draining. |
| 1601 | Warning | Post-report action evaluation failed after claim. |
| 1602 | Warning | Post-report action evaluation failed while draining. |
| 1701 | Warning | Post-report workflow failed after its evaluation lease was lost. |
| 1702 | Warning | A post-report action intent lost its evaluation lease; the attempt is abandoned without writing a result. |
| 1801 | Information | Retention is switched off by configuration; this host compacts no payload, reaps no artifact and deletes no abandoned workspace. |
| 1802 | Warning | A retention pass failed before any of its operations could run; the next pass follows after backoff. |
| 1901 | Warning | Signal payload compaction failed; the other retention operations still ran and compaction is retried next pass. |
| 1902 | Warning | Attempt artifact reaping failed; it is retried next pass. |
| 1903 | Warning | Abandoned remediation workspace reaping failed; it is retried next pass. |

The three "failed after claim" events (1401, 1501, 1601) also cover a cancellation the host did not
request. Each pump links its per-item cancellation to the host token and removes items from its task
set before draining cancels them, so a cancellation observed outside host shutdown means claimed,
leased work was abandoned for an unrequested reason and is reported rather than dropped. The
"failed while draining" events (1402, 1502, 1602) do not: draining requests that cancellation itself.

### Infrastructure adapters (2000-2999)

| Id | Level | Meaning |
| --- | --- | --- |
| 2101 | Warning | Triage configuration snapshot was not persisted because PostgreSQL is not configured. |
| 2201 | Error | Fault was not terminalized while publishing a triage report. |
| 2301 | Warning | Memory seed runtime synchronization failed with a bounded failure type. |
| 2302 | Warning | Memory seed failure status persistence was skipped. |
| 2401 | Warning | Telegram notification provider returned a bounded failure code. |
| 2501 | Warning | GitHub issue provider returned a bounded failure code. |

### Application (3000-3999)

| Id | Level | Meaning |
| --- | --- | --- |
| 3001 | Debug | Application request dispatched with its elapsed time. |
| 3002 | Warning | Application request dispatch failed. |
| 3101 | Warning | Triage job attempt failed, with its bounded error code and exception type. |
| 3102 | Information | Attempt failure resolved to retry-pending with its next attempt time. |
| 3103 | Error | Attempt exhausted its retry budget and was dead-lettered. |
| 3104 | Warning | Attempt was delayed because the model provider is unavailable; the attempt budget was not consumed. |
| 3105 | Error | Attempt failure could not be recorded durably by the runtime repository. |
| 3201 | Information | Model call completed, with route, call kind, provider, configured provider (or `unknown` when the route named none), model, usage source, token counts, duration and proposed tool-call count. |
| 3202 | Warning | Model call failed with a bounded exception type. |
| 3203 | Warning | Model call was cancelled because the attempt wall-clock budget ran out. |
| 3204 | Information | Model call was cancelled by host shutdown. |
| 3205 | Warning | Model call failed on its route and is being retried once on that route's fallback, with both route IDs and the failed call's error code. |
| 3206 | Warning | Fail-over was not attempted because the failed call's accounting could not be made durable. |
| 3211 | Debug | Model tokens were charged to the attempt budget. |
| 3212 | Warning | Attempt budget limit was reached, with its bounded reason token. |
| 3301 | Warning | Worker tool call was denied, with its bounded denial token. |
| 3302 | Information | Worker tool call was not executed because it requires approval. |
| 3303 | Debug | Worker tool call executed successfully. |
| 3304 | Warning | Worker tool call ended in a non-success status with a bounded error code. |
| 3401 | Information | Orchestrator was reprompted, with its specific closed reason, bounded reprompt counter and durable `BudgetEvent` ledger record. |
| 3402 | Warning | Worker role output was reprompted, with job, attempt, role, a safe validator diagnostic list, bounded reprompt counter and durable `BudgetEvent` ledger record. |
| 3403 | Warning | Orchestrator spent its bounded reprompt allowance, with the closed reason and safe diagnostic of the turn it could not correct. The attempt then dead-letters as `triage_budget_orchestrator_reprompt_limit_reached`. |
| 3501 | Debug | Immediate tool policy allowed a worker tool. |
| 3502 | Warning | Immediate tool policy denied a worker tool. |
| 3503 | Information | Immediate tool policy requires approval for a worker tool. |
| 3511 | Information | Post-report action policy allowed a proposal in its effective mode. |
| 3512 | Warning | Post-report action policy denied a proposal. |
| 3513 | Information | Post-report action policy requires approval for a proposal. |
| 3601 | Error | A configured redaction pattern exceeded its match timeout; the field was replaced with the timeout marker. Carries the pattern name and field path only, never the field value. |
| 3701 | Information | Signal payload compaction emptied a bounded number of raw payloads received before its cutoff. |
| 3702 | Information | Attempt artifact retention reaped a bounded number of artifacts created before its cutoff. |
| 3703 | Information | Abandoned remediation workspace reaping finished, with its closed outcome code and the counts it examined, deleted, left alone and could not delete. Never a host path. |
| 3801 | Information | A remediation pass produced a diff, with job, attempt, report and diff ids, the file count, the diff's byte count and the fixed statement that no test was executed. Never the diff itself. |
| 3802 | Warning | A remediation answer was refused and the model was reprompted, with job, attempt, a bounded reprompt counter and the closed outcome code. Never the answer, a path or a file line. |
| 3803 | Warning | A remediation pass produced no diff, with job, attempt, report and the closed outcome code it gave up on. |
| 3804 | Information | A recorded remediation diff was frozen into a `code_write` proposal waiting for human approval, with job, report, the diff's byte count, the base tree identity and whether the proposal already existed. Never the diff itself. |
| 3805 | Warning | A recorded remediation diff produced no approvable proposal, with job, report and the closed outcome code. Never a path, a file line or a byte of the diff. |
| 3806 | Error | A remediation proposal was created already approved, which no shipped policy allows for a `code_write` action. Carries job and report only. |
| 3807 | Warning | An approved remediation action was refused before anything was changed, with the action id and the closed outcome code. A moved checkout arrives here. |

### API host (4000-4999)

| Id | Level | Meaning |
| --- | --- | --- |
| 4001 | Error | A domain exception reached the API error boundary, with its correlation id and error code. |
| 4002 | Warning | A client-facing `NotFoundException`, `ConflictException`, `ForbiddenRequestException` or `ValidationException` reached the API error boundary, with its correlation id and error code. |
| 4003 | Warning | An OTLP export carried more records than `IngestionLimits:MaxSignalsPerExport` allows and was rejected before any signal was ingested, with the signal kind, the observed record count, the configured limit and the stable code `otlp_export_signal_limit_exceeded`. |

### Level policy

A denial, a dead-letter, a lost lease, an exhausted budget and invalid worker output that needs
correction are at least `Warning`, because an
operator has to see them. A routine allow, a successful tool call and a token charge are `Debug`,
so an ordinary investigation does not fill the log with policy noise. Outcomes an operator wants in
a normal production log without enabling debug output - a completed model call with its token and
duration figures, a scheduled retry, an approval requirement, a reprompt - are `Information`. Only a
state that ends or corrupts a unit of work is `Error`: a dead-lettered attempt, a failed durable
failure write, a non-terminalized fault and an unhandled domain exception at the API boundary.

### What these events never carry

Log events carry bounded, non-sensitive facts only: job, fault and correlation ids, role and route
names, tool names, decision outcomes, reason tokens, token counts, durations, attempt numbers,
bounded error codes and exception type names. They never carry message content, rendered prompts or
responses, artifact payloads, tool arguments or results, memory document text, embedding vectors,
credentials or raw provider error strings.

The worker-output reprompt event is a narrow exception to the general absence of validation detail:
it carries at most 20 validator diagnostics. Each uses fixed validator wording and an
application-selected path into the configured output schema. An unexpected, model-controlled property
is represented by its count without its name. This is not a claim that arbitrary output property names
are safe. The event and its ledger record never carry model output, unknown property names,
`JsonException` text or path, the correction prompt or the output schema. Provider and parser
exception messages are reduced to a closed classification token before they reach a log; the durable
job row uses the same reduction (see "Triage job failure classification" below), not exception text.

### API error responses

The API error boundary (`ApiExceptionHandler`, `ApiErrorMapping`) never places an exception's own
`Message` in an HTTP response. A `NotFoundException`, `ConflictException`, `ForbiddenRequestException`
or `ValidationException` reaching a request is mapped to a `ProblemDetails` body with an authored,
client-safe `detail` and a stable `errorCode` extension field; an unrecognized `DomainException` maps
to a fixed "the request could not be completed" detail under `internal_domain_violation`. The mapping
from exception type to HTTP status and default code/detail lives in one place, `ApiErrorMapping`; a
throw site may attach a more specific `Code`/`Detail` pair (see `ApplicationErrorCodes`) when it knows
which resource or rule was involved, without changing where the status is decided. Every mapped
exception is logged once, server-side, with the request's `HttpContext.TraceIdentifier` as its
correlation id and the resolved `errorCode` (events 4001-4002 above); the exception object is attached
because these messages are authored by this codebase, not raw provider or infrastructure text.

### Triage job failure classification

`last_error_message` on `incidentcompass.triage_jobs` is a bounded classification, not raw exception
text a provider or validator may have produced. The ordinary form is `"<error code>: <exception type
name>."`, using the exact code already stored in the sibling `last_error_code` column. The immediate
`worker_output_invalid` terminal outcome instead stores the fixed content-free message
`worker_output_invalid: worker output remained invalid after bounded reprompts.`, with no exception
type. The delayed provider-outage state and the attempt-limit guard likewise store their own fixed
sentences, `Triage delayed: provider unavailable.` and `triage_job_attempt_limit_exhausted: attempt
guard.`. Permanent budget/governance codes from `TriageNonRetryableFailureClassifier` and the generic
`triage_job_attempt_failed`/`config_snapshot_unavailable` codes retain the ordinary classification and
the existing `TextTruncator` bound. A row is therefore self-explanatory when read directly from the
database, and a provider response body or model output cannot end up stored in this column.

The triage job attempt failure event (3101) and its disposition event (3102/3103/3104) are written
before the durable attempt-failure write is attempted, so a failing durable write (3105) can never
erase the trace of what originally failed.

### Why a waiting job is waiting

`GET /api/v1/faults/{id}` returns that classification to the caller so an operator does not have to
read the ledger to learn why a job has not finished. Its `job` object carries two fields beyond the
job identity:

- `lastErrorCode` - the durable `last_error_code` value, or `null` when no attempt has failed. It is
  the job's most recently recorded attempt outcome for every status, not a provider-outage special
  case: a field populated for exactly one code would force a caller to switch on the code before
  trusting the field, and would go silent on the dead-lettered jobs an operator most needs a reason
  for. The vocabulary is closed and application-owned (`ProviderErrorCodes`,
  `TriageNonRetryableFailureClassifier`, the two generic codes above).
- `nextAttemptAtUtc` - the durable `next_attempt_at_utc` value, or `null` when no retry is scheduled.
  Claiming a job and dead-lettering it both clear the column, so the field is populated only while
  the job is actually waiting for a scheduled retry.

`status` remains the authority on whether the job is finished: a provider outage reads as
`RetryPending` + `provider_unavailable` + a future `nextAttemptAtUtc`, which is a delay with an
until-when, not a terminal failure. The outage path also does not consume the attempt budget, so
`attempt` does not advance while the provider is down.

`last_error_message` is deliberately **not** projected. It is content-free by construction, but its
ordinary form appends the raising exception's type name, and an internal exception type is not
something a caller of the public fault endpoint has any reason to receive. The disclosure boundary
therefore stops at the closed code vocabulary and a timestamp.

## ModelCall Ledger Events

The live model telemetry mechanism is the append-only triage ledger. Each investigation model call writes a compact `ModelCall` event to `incidentcompass.triage_ledger`.

`ModelCall` rationale stores redacted metadata only:

- call kind;
- route ID;
- provider, the adapter that answered;
- nullable configured provider ID (`providerId`), the provider table entry the route named;
- model;
- nullable input, output and total token counts;
- usage source (`provider`, `estimate` or `unknown`);
- duration in milliseconds;
- proposed tool-call count;
- stable call ID;
- outcome (`success` or `failed`);
- nullable safe error code;
- nullable provider-reported reasoning token count;
- nullable fail-over route ID (`fallbackForRouteId`).

That payload is the named `ModelCallLedgerMetadata` record. Its JSON property names, casing and order
are pinned by attribute because ledger rows and the cost-rollup reader share this persisted contract.
The call ID, outcome and nullable error code extend the earlier success-only shape, while the existing
field names remain stable. Unknown usage is represented by nullable token fields rather than a
fabricated estimate.

`routeId` always names the route that was actually called. On a call that failed over to a route's
declared fallback (see `docs/model-gateway.md`, "Route Fallback") that is the fallback route, and
`fallbackForRouteId` names the route it answered for; on every other call the property is absent, so
rows written before the field existed are byte-identical to rows written after it. A fail-over
therefore appears in the ledger as two `ModelCall` rows: the failed call on the configured route,
with its own error code and its own `BudgetEvent` charge, and the successful call on the fallback
route. Neither is refunded, exempted or merged into the other.

`provider` and `providerId` are two different facts and both are recorded. `provider` is the adapter
that produced the answer, which is one string shared by every endpoint of that kind the host can
reach; `providerId` names the entry in the triage configuration's provider table that the called
route used, which is what has its own endpoint, its own credential and its own prices. Cost
accounting keys on `providerId` (see `docs/cost-tracking.md`), because two configured providers
answered by one adapter are two payers. The property is absent on a row written before it existed and
on a call whose route named no provider, so rows written before the field existed are byte-identical
to rows written after it, and a reader that needs a payer has to decide what to do with a row that
names only an adapter.

The ledger does not store rendered prompts, full provider responses, document text, provider credentials, API keys, embedding vectors or reasoning text. A numeric provider-reported reasoning token count may be stored in `ModelCall` metadata, but no reasoning text is logged or persisted. Token budget accounting is recorded separately as first-class `BudgetEvent` rows with `tokens_delta` and `workers_delta` columns. Every worker or orchestrator correction turn also writes one bounded `BudgetEvent`. A worker correction uses the `worker_output_reprompt:` rationale prefix, retains the role and is capped at 1,000 characters; it contains safe diagnostics, not validation exception text, model output, prompt or schema. A
post-report remediation correction writes the same kind of row under the `remediation_patch_reprompt:`
prefix with the role `remediation`, and carries only the closed outcome code that caused it: never the
diff the model sent, a path, or a line of a file.

`ModelCall` rows carry a `kind` naming what the call was for. `orchestrator` and `worker` are the
investigation kinds; `remediation` is the post-report call that asks for a unified diff. The
remediation call runs on the same bounded caller, so it is admitted, deadlined, charged and accounted
exactly as the others are, and cost roll-ups can separate what an incident spent producing its report
from what it spent proposing a change by grouping on that one field.

`ModelCall` rows and token-accounting `BudgetEvent` rows are mirrored by bounded application log events 3201-3206 and 3211-3212 above, while reprompt `BudgetEvent` rows are mirrored by events 3401, 3402 and 3802, so live model observability is readable from logs and auditable from the ledger.

### Report Model Provenance

A published report says which models produced it. One investigation is many model calls - one or
more orchestrator turns, one turn per delegated role plus that role's own tool and correction turns,
and any reprompt turns - and a role names its own route, so a single attempt can be answered by
several routes, providers and models. There is therefore no single "the model that wrote this
report", and `triage_reports.model_provenance` holds a list rather than a name.

Each element is one distinct combination of call kind, role, route, adapter, configured provider and
model that answered during the publishing attempt, with how many calls it answered, in the order each
combination first answered. Two models used for the same role are two elements, so the claim stays
true if a later change lets one role run on more than one route. The configured provider is part of
the combination as well as the adapter, because one adapter answers for every provider of its kind a
host declares: without it, two providers with different endpoints and credentials answering the same
model name would be reported as one participant. `providerId` is `null` on an element derived from a
ledger row written before that was recorded, which is not the same claim as naming a provider. The
element shape is the public
`TriageReportModelParticipant` record and reaches API callers as `modelProvenance` on
`GET /api/v1/triage-reports/{id}` and `GET /api/v1/faults/{faultId}/triage-report`.

The value is derived inside the publish transaction from that attempt's own `ModelCall` ledger rows,
which record what actually answered rather than what the route asked for. It is a projection of the
ledger with one producer rather than a second accumulator kept in step by hand, and no part of it
comes from model output: a `publish_report` body that asserts its own provenance is ignored. Only
successful calls are counted, because a failed call produced nothing the report is built on and its
accounting belongs to the attempt that failed.

A call answered by a route's fallback is therefore listed under the fallback route and its model, and
the call it replaced is not listed at all. That is why provenance is not, on its own, how a reader
learns that a run was degraded: an attempt legitimately spans several routes, so a second route in
this list is not by itself evidence that anything failed. The report states that separately, as a
backend-owned limitation described in `docs/model-gateway.md`, "Route Fallback".

It is stored on the report as well as in the ledger because a report row is immutable and never
deleted while ledger rows carry no such guarantee, and a report that could stop being able to name
its own models is not a durable claim. The `ReportPublished` ledger row is deliberately not given a
second copy: it already points at the report through `payload_ref`.

`model_provenance` is `null` for a report published before provenance was recorded. Reports are
immutable, so those rows cannot be backfilled, and `null` is deliberately not the same claim as the
empty list, which means provenance was derived and the attempt recorded no model call.

## Reading A Timeline After Retention

`GET /api/v1/faults/{id}/ledger` reconstructs a fault's whole appended event timeline, and it keeps
working after attempt-artifact retention has removed payloads those events reference. The ledger
stores only a compact `payload_ref` with no foreign key, and the reader resolves it as a scalar
expression rather than a join, so no event is ever dropped because the payload behind it is gone.

Not crashing is the smaller half. Each event also carries `payloadState`, answered at read time:

| Value | What it says | What backs it |
| --- | --- | --- |
| `None` | The event references no payload. | `payload_ref` is `NULL`. |
| `Retained` | The referenced attempt artifact is still stored and can still be read. | The artifact row exists at read time. |
| `Reaped` | The referenced attempt artifact is gone, and retention is what removed it. | See below. |
| `NotReapable` | The reference does not name an attempt artifact, so retention cannot remove what it points at. | `report:` and `action:` references name append-only records whose delete triggers reject removal outright; a `model-call:` reference is a correlation key with no stored payload. |

`Reaped` is the one value that claims more than the row in front of it shows, so it is worth being
explicit about what makes it true. An `artifact:` reference is only ever written by
`PostgresTriageToolResultCommitter`, which inserts the artifact row and the ledger row in one
transaction, so such a reference never named a row that did not exist; and the reap in
`PostgresAttemptArtifactRetentionRepository` is the only statement in the system that deletes from
`triage_artifacts`. Both halves are properties of the writers rather than database constraints. A
future second deleter would weaken this value back to meaning "absent", which is why it is recorded
here rather than left to be assumed.

This field is why the timeline is honest rather than merely intact: before it, a reference whose
payload had been reaped and one whose payload was still there rendered as the same string, and a
reader had no way to tell which it was holding.

## Correlating An Incident With External Resources

`GET /api/v1/action-approvals` reads the compact external-action audit projection in two directions,
both tenant-scoped by the authenticated caller's tenant and both paged by the same cursor:

- `?externalResourceKind=github_issue&externalResourceId=42` - the exact pair lookup: which governed
  actions produced this external resource. The pair must be exact and well formed; a partial or
  unrecognized pair is rejected rather than widened.
- `?faultId={id}` - the inverse: which external resources this incident's governed actions touched,
  including actions that were rejected or expired and so never reached an external system, because
  "we proposed this and did not do it" is part of the answer.

Each item carries `faultId`, which is what makes a pivot possible: an operator holding a GitHub issue
number reaches the incident through the first query and the rest of that incident's external
footprint through the second.

Neither direction renders a raw provider payload. `result_payload` holds the provider's own response
body and is never projected into either response; what a caller sees is the bounded
`resultSummary` plus the four projection columns (`externalResourceKind`, `externalResourceId`,
`externalBeforeState`, `externalAfterState`), whose vocabulary is closed by check constraint.

The fault predicate carries no tenant term of its own. It composes with the tenant predicate that
already scopes every shape of this query, so a fault id belonging to another tenant returns output
byte-identical to a fault id that does not exist at all, and cannot be used to learn that another
tenant's fault is real.

## Hourly Cost Rollups

`GET /api/v1/observability/cost-rollups` reads these durable `ModelCall` rows for the authenticated
tenant over required `fromUtc` and `toUtc` values. Both boundaries must use a UTC offset. Start is
inclusive, end is exclusive, and the non-empty window is limited to 31 days. Results contain only UTC
hour, call count, input/output/total token totals, priced/unpriced call counts, estimated-usage call
and token counts, and exact spend totals separated by currency.

Each call is priced only when its metadata is valid, it names the configured provider that answered,
exactly one case-sensitive `providerId`/model pricing interval contains the call timestamp, and its
`usageSource` is `provider`. Valid calls without a price still contribute token totals and increment
`unpricedCallCount`. Invalid JSON or types, blank identities, unsafe token values and overlapping or
tied prices increment the call and unpriced counts but contribute no token or spend value. A real
configured zero price remains a priced call; missing or ambiguous pricing never becomes false zero
spend.

Locally estimated token counts are never priced, so a spend figure is built only from counts the
provider itself reported and can be compared against a provider invoice without mixing measurement
with approximation. `estimatedUsageCallCount` and `estimatedUsageTotalTokens` say how much of the
hour that excluded; both are subsets of `callCount` and `totalTokens`. A row that names only an
adapter, which is how rows were written before the configured provider was recorded, counts and
contributes its tokens but is never priced, because an adapter name is shared by every provider
behind it. `docs/cost-tracking.md` is the contract for what an operator can and cannot conclude from
a spend figure.

Every durable `ModelCall` row increments `callCount`, including unsuccessful calls. An unsuccessful
call with unknown usage has null token fields, increments `unpricedCallCount`, and contributes no
input, output or total tokens and no spend. This preserves visibility that a call occurred without
inventing usage or cost.

The pricing table is effective-dated operator-maintained database configuration. This release adds no
price-management API, configuration reload, currency conversion, threshold, alert job or notification.
The rollup response and dispatch logs do not return ModelCall provider, model, route ID, prompt,
response, credential, endpoint or embedding data. ModelCall and BudgetEvent metadata and the
fault-ledger response remain bounded and contain no prompt or response bodies.

Post-report approval state uses the exact `ActionProposed`, `ApprovalDecision`,
`ActionDispatchStarted` and `ActionCompleted` ledger events. These rows carry bounded summaries,
closed decisions/statuses and `action:<id>` or `artifact:<id>` references. They do not copy canonical
payload bodies, provenance bodies, adapter routes, credentials, prompts or transcripts into the
ledger or application logs.

Every tool-policy denial writes its reason in one shape: a stable reason code, optionally followed by
`: ` and a human detail. `rate_cap_exceeded: memory_search used 3/3 in attempt scope` is one row;
`tool_not_granted_to_role` with no detail is another. The code is always the leading token, so
denials in `PolicyDecision(Denied)` rows aggregate by cause without parsing the prose after it, and
the same code leads the reason on the matching log event. Allowed and approval-required decisions
keep a descriptive reason instead: they name every rule that matched, which is not a single cause.

Denied post-report proposals use `PolicyDecision(Denied)` with a closed bounded reason and a safe
`report:<id>` reference only after same-tenant current origin resolution. They do not create action,
artifact or provenance rows. Rejections before that origin boundary write no ledger row, avoiding a
foreign-report oracle. Accepted proposal rate caps count `ActionProposed`, not only allowed policy
decisions, so requested and auto-approved proposals consume the same cap.

Post-report evaluation intents deliberately add no new triage-ledger event kind. Their immutable
identity, fenced processing state, database-clock lease, attempt count, next retry time and bounded
closed error code remain inspectable in `incidentcompass.post_report_action_intents`; any accepted
proposal then uses the existing action events above. Intent input contains only identifiers, exact
tool/workflow version and an optional bounded route id. Logs must not copy its bytes, report or
evidence bodies, prompts, provider responses, credentials or adapter routes.

Approved dispatch writes `ActionDispatchStarted` in the same transaction as its durable owner/fence
claim. Definitive success or failure writes one bounded `ActionResult` and `ActionCompleted` atomically
with terminal state. Dry-run uses the same terminal evidence with zero adapter calls. Exceptions,
timeouts, cancellation and expired in-doubt claims use the closed `dispatch_outcome_unknown` failure;
logs and ledger rows do not contain frozen payload bytes, provider bodies, credentials or routes.
Confirmed live Telegram and GitHub success additionally stores a compact typed projection in that
same terminal transaction. It contains only external resource kind/id and one closed state change:
`not_sent` to `sent`, `absent` to `open`, or `open` to `comment_added`. Dry-run, definitive failure and
outcome-unknown leave the projection null and remain observable through their stable `ActionResult`
and `ActionCompleted` outcome. Projection fields are immutable after terminal commit.
For Telegram notifications, an unclaimed predecessor that is replaced records the existing bounded
superseded terminal evidence. A started predecessor denies a successor. Confirmed live success and
outcome-unknown start a 30-minute database-clock cooldown measured from durable dispatch start;
simulated, requested, rejected, expired and definitive pre-mutation failures do not. Telegram success
keeps only the bounded provider kind and
message id in the action result, never the token, chat id, request path or raw response.

GitHub ticket create uses the same action events and stores only a bounded canonical provider kind
and issue number on confirmed success. Repository-bound no-match eligibility is proven from the
durable current-attempt `ToolResult`; proposal denials use closed reason codes. Preflight and create
failures expose only stable codes, and a response that becomes unreadable after the single POST is
`dispatch_outcome_unknown`. Correlation markers may be read from frozen payload/history for bounded
duplicate lookup, but tokens, Authorization headers, repository authority, request paths and raw
provider bodies never enter ledger rows or logs.

GitHub ticket update uses a distinct action category and stores only bounded canonical provider,
issue-number and comment-id metadata on confirmed success. The cited `ExistingTicket` target is
resolved and rechecked from durable report evidence; missing, foreign, malformed or ambiguous targets
produce stable closed failures without a provider write. Target/comment preflight and POST failures
expose only stable codes. The frozen comment body and marker may be used for exact duplicate lookup,
but neither they nor raw provider bodies, credentials, headers, repository authority or request paths
enter ledger rows or logs. An unreadable or interrupted response after comment POST begins is recorded
as `dispatch_outcome_unknown` and is not resent.

## Failure Behavior

Model calls are part of the Worker investigation loop. Required durable side effects, including ledger budget/model-call events and final report commit events, are treated as part of the workflow state. The Worker does not expose foreground success to an API caller after a missing required durable write.

Provider failures are normalized at the Application port boundary and recorded through job failure state and application logs rather than through a separate AI request-log table. Provider transport failures use the durable `provider_unavailable` delayed state instead of consuming the ordinary attempt limit. The per-process Worker outage tracker pauses claims after its configured threshold and clears on a successful model call; this state contains no prompt, credential or incident data.

## Runtime Metadata Telemetry

`IncidentCompass.Runtime` exposes an in-process `ActivitySource` and `Meter` for job claims and attempts, model calls and duration, governed tool calls, PostgreSQL migrations and memory synchronization. It is a source only: the current release does not configure an OTLP runtime exporter, collector endpoint or metrics endpoint. A host may attach a compatible listener or exporter without changing application workflows.

The source uses fixed operation names and a closed `outcome` vocabulary: `claimed`, `succeeded`, `failed`, `cancelled`, `provider_unavailable` and `denied`. It never attaches incident IDs, fault IDs, tenant IDs, user IDs, service names, prompt text, document text, tool arguments, provider responses, credentials or connection strings as telemetry tags. Listener and exporter callback failures are isolated so triage, migrations and memory synchronization continue according to their normal durable-workflow behavior.

## Later Audit Events

The ledger records the action lifecycle that exists today. The events below have no durable audit
record because the behavior behind them is not implemented; each needs its records added with the
feature rather than reconstructed from logs afterwards:

- quota exceeded;
- cost alert delivery and quota enforcement built on independently governed policy.

External-action correlation is no longer on that list: the resource and fault directions described in
"Correlating An Incident With External Resources" both read the existing projection and lifecycle
events, and neither needed a new audit record.

## OTLP Ingress

OTLP ingress is separate from IncidentCompass runtime telemetry. The API accepts OTLP/HTTP protobuf
trace and log exports at `/v1/traces` and `/v1/logs`, maps only the signal fields needed for deterministic
intake, and preserves trace, span, parent span, service, operation and error metadata. This makes
IncidentCompass a consumer of an observability pipeline, not an observability backend. It does not expose
an OTLP runtime exporter, a metrics receiver or a profile receiver in this release.

OTLP ingestion is bounded per request in two independent ways: `IngestionLimits:MaxPayloadBytes`
caps the bytes one export may carry, and `IngestionLimits:MaxSignalsPerExport` caps the records it
may carry. The record bound is checked on the parsed export before mapping and before any command is
dispatched, so an over-limit export is rejected whole and stores nothing. Because these endpoints
answer in protobuf rather than `ProblemDetails`, a rejection returns `413 Payload Too Large` with the
empty body the other OTLP failure paths already use; the stable code
`otlp_export_signal_limit_exceeded` is recorded in log event 4003 rather than in the response.
That log carries the signal kind, the observed record count and the configured limit only - never
record bodies, span names, attributes or resource attributes.

## Later Options

`IncidentCompass.Runtime` is a telemetry source with no exporter, so making it visible is host work
this release does not do. These are the obvious next steps, none of which this release provides:

- configurable OTLP exporter wiring and collector examples;
- metrics endpoint;
- Prometheus/Grafana example;
- Azure Application Insights adapter.
