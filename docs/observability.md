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
| 3201 | Information | Model call completed, with route, call kind, provider, model, usage source, token counts, duration and proposed tool-call count. |
| 3202 | Warning | Model call failed with a bounded exception type. |
| 3203 | Warning | Model call was cancelled because the attempt wall-clock budget ran out. |
| 3204 | Information | Model call was cancelled by host shutdown. |
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
type. Permanent budget/governance codes from `TriageNonRetryableFailureClassifier`,
`provider_unavailable` and the generic `triage_job_attempt_failed`/`config_snapshot_unavailable`
codes retain the ordinary classification and the existing `TextTruncator` bound. A row is therefore
self-explanatory when read directly from the database, and a provider response body or model output
cannot end up stored in this column.

The triage job attempt failure event (3101) and its disposition event (3102/3103/3104) are written
before the durable attempt-failure write is attempted, so a failing durable write (3105) can never
erase the trace of what originally failed.

## ModelCall Ledger Events

The live model telemetry mechanism is the append-only triage ledger. Each investigation model call writes a compact `ModelCall` event to `incidentcompass.triage_ledger`.

`ModelCall` rationale stores redacted metadata only:

- call kind;
- route ID;
- provider;
- model;
- nullable input, output and total token counts;
- usage source (`provider`, `estimate` or `unknown`);
- duration in milliseconds;
- proposed tool-call count;
- stable call ID;
- outcome (`success` or `failed`);
- nullable safe error code;
- nullable provider-reported reasoning token count.

That payload is the named `ModelCallLedgerMetadata` record. Its JSON property names, casing and order
are pinned by attribute because ledger rows and the cost-rollup reader share this persisted contract.
The call ID, outcome and nullable error code extend the earlier success-only shape, while the existing
field names remain stable. Unknown usage is represented by nullable token fields rather than a
fabricated estimate.

The ledger does not store rendered prompts, full provider responses, document text, provider credentials, API keys, embedding vectors or reasoning text. A numeric provider-reported reasoning token count may be stored in `ModelCall` metadata, but no reasoning text is logged or persisted. Token budget accounting is recorded separately as first-class `BudgetEvent` rows with `tokens_delta` and `workers_delta` columns. Every worker or orchestrator correction turn also writes one bounded `BudgetEvent`. A worker correction uses the `worker_output_reprompt:` rationale prefix, retains the role and is capped at 1,000 characters; it contains safe diagnostics, not validation exception text, model output, prompt or schema.

`ModelCall` rows and token-accounting `BudgetEvent` rows are mirrored by bounded application log events 3201-3204 and 3211-3212 above, while reprompt `BudgetEvent` rows are mirrored by events 3401 and 3402, so live model observability is readable from logs and auditable from the ledger.

## Hourly Cost Rollups

`GET /api/v1/observability/cost-rollups` reads these durable `ModelCall` rows for the authenticated
tenant over required `fromUtc` and `toUtc` values. Both boundaries must use a UTC offset. Start is
inclusive, end is exclusive, and the non-empty window is limited to 31 days. Results contain only UTC
hour, call count, input/output/total token totals, priced/unpriced call counts and exact spend totals
separated by currency.

Each call is priced only when its metadata is valid and exactly one case-sensitive provider/model
pricing interval contains the call timestamp. Valid calls without a price still contribute token
totals and increment `unpricedCallCount`. Invalid JSON or types, blank identities, unsafe token
values and overlapping or tied prices increment the call and unpriced counts but contribute no token
or spend value. A real configured zero price remains a priced call; missing or ambiguous pricing never
becomes false zero spend.

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
- additional external-action before/after correlation beyond the existing action lifecycle events;
- cost alert delivery and quota enforcement built on independently governed policy.

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
