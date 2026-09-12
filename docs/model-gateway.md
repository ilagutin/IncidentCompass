# Model Gateway

Application use cases must not call provider SDKs directly. Model access goes through application-owned abstractions implemented by Infrastructure.

## Model Client

```csharp
public interface IAiModelClient
{
    Task<AiModelResponse> CompleteAsync(
        AiModelRequest request,
        CancellationToken cancellationToken);
}
```

Implemented adapters:

- OpenAI-compatible client, including local OpenAI-compatible endpoints, which is the normal
  local/demo runtime path;
- mock/fake client for tests and explicit mock-only checks.

Possible future adapters:

- Azure OpenAI;
- Anthropic;
- Google Gemini.

## Embedding Client

```csharp
public interface IEmbeddingClient
{
    Task<EmbeddingResponse> CreateEmbeddingAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken);
}
```

Implemented adapters:

- OpenAI-compatible embedding client;
- mock embedding client.

Application use cases call `IEmbeddingClient` through the Application layer. The OpenAI-compatible provider is the normal local/demo runtime path and uses the configured embeddings endpoint, model, timeout and retry settings. The mock provider is for tests and explicit mock-only checks.

Embedding requests keep a wider idempotent retry boundary than chat generation, because an embedding
request has no side effect: HTTP 408, 429 and every 5xx response except 501/505, plus configured
timeouts and transport failures, are retried up to the embedding retry limit. HTTP 501 and 505 are
not retried, on either path. They describe a request the endpoint will never accept, so replaying it
only spends attempts and delay before the same terminal answer. The retry predicate and the terminal
classification are derived from one rule, so a status classified as `RejectedRequest` is never also
replayed. Terminal embedding failures are cause-classified. HTTP 429 and retryable 5xx responses and
positively safe pre-dispatch transport failures are `Unavailable`; HTTP 408 and an exhausted
configured timeout are `GenerationTimeout`; HTTP 501/505, other 4xx responses and configuration
errors are `RejectedRequest`; other exhausted transport failures, including connection reset and
response-ended failures, are `TransportFailure` with `transport_error`; and invalid JSON or an empty
vector is `InvalidResponse`. Caller cancellation propagates unchanged.

For both OpenAI-compatible adapters, `TimeoutSeconds` bounds one HTTP attempt. The adapter owns that
deadline through its linked cancellation token, including configured values up to 3600 seconds. The
typed `HttpClient` has no separate deadline, so its framework default cannot end a valid long-running
attempt early. `MaxWallClockSeconds` is a distinct investigation-attempt budget:
`InvestigationModelCaller` checks it before each model call and passes the remaining attempt budget
into that call through linked cancellation.

## Requirements

- Model name is configurable.
- The chat-generation per-HTTP-attempt timeout is configurable and defaults to 300 seconds.
- The embedding per-HTTP-attempt timeout is separately configurable and defaults to 30 seconds.
- Chat-generation retries are limited to HTTP 429/503 responses and failures that are positively known
  to occur before dispatch: name resolution, secure-connection establishment, proxy-tunnel
  establishment, or a connection error whose socket cause is connection refused, timed out, host
  unreachable, network unreachable, host not found or address not available. A timeout, reset,
  response-ended failure, generic connection error or another HTTP response is not automatically
  replayed.
- `Retry-After` delta and date values are honored for retryable responses up to
  `MaxRetryDelaySeconds`. Chat generation and embeddings each carry their own ceiling under their own
  configuration section. Both default to 5 seconds, are configurable from 1 through 3600 seconds and
  also cap the exponential fallback used when the header is absent or no longer in the future.
- HTTP 501/505 responses are not retried on either path, because both paths classify them as
  `RejectedRequest`.
- Token usage is captured when returned by the provider. Budget accounting uses provider usage when present and a compact backend estimate otherwise, recording the source in the ledger.
- Provider errors are normalized into the application-owned failure kinds `Unavailable`,
  `RejectedRequest`, `GenerationTimeout`, `OutputLimitReached`, `AmbiguousInterruption`,
  `TransportFailure`, `InvalidResponse` and `Unknown`. The base provider-exception type alone does
  not imply an outage.
  Model-provider outages are retried as an explicit `provider_unavailable` delayed job state. After
  the configured consecutive-failure threshold, each Worker process pauses new claims for
  `IncidentCompass:ProviderResilience:BackpressureSeconds`; a successful model call clears that
  local pause, and a call answered by a route's fallback deliberately does not. This is in-process
  backpressure, not cross-host coordination. The delayed state is visible to callers: `GET /api/v1/faults/{id}` returns the
  job's `lastErrorCode` and `nextAttemptAtUtc` alongside its `RetryPending` status, without exposing
  any provider-authored text (see `docs/observability.md`, "Why a waiting job is waiting").
- Request/response objects carry correlation IDs.
- OpenAI-compatible chat completions include one `Idempotency-Key` header per
  high-level model request and reuse it across retry attempts.
- Tests use mock clients by default.
- OpenAI-compatible provider URLs use HTTPS by default; insecure HTTP is allowed only for explicit loopback development configuration.

## Provider Response And Failure Boundary

The OpenAI-compatible adapter fails closed when a response cannot be mapped to the provider-neutral
contract. A completion with no usable content or tool call and `finish_reason: length` becomes
`provider_output_limit_reached`; `empty_response` is reserved for a genuinely empty completion. A
tool call must contain its provider-issued id, use the `function` type, name a function and carry
arguments that parse as a JSON object. The adapter does not fabricate an id, discard a malformed
tool call or wrap unparsable arguments as a string.

Caller cancellation propagates unchanged. A provider-owned deadline becomes
`provider_generation_timeout`. The positively safe pre-dispatch failures listed above become
`provider_unavailable`; a generation interruption whose dispatch outcome is not known becomes
`provider_dispatch_outcome_unknown`. Automatic redirects are disabled so a redirected POST is not
silently replayed outside this boundary.

These causes have explicit Worker dispositions. Only `Unavailable` enters provider-outage tracking,
process-local backpressure and the delayed no-attempt-cost path. Rejected requests, output-limit
exhaustion and ambiguous post-dispatch interruptions dead-letter immediately. Generation timeouts
and invalid, unknown or embedding `TransportFailure` failures consume the normal finite attempt
budget. An embedding transport failure is `RetryPending` while attempts remain and `DeadLettered`
at `MaxAttempts`. A pre-processing attempt guard also dead-letters a reclaimed job whose attempt is
already beyond `MaxAttempts`.

One kind still maps to one disposition when a route declares a fallback. The second call happens
inside the governed model call rather than in the job runner, and a fallback that fails as well
raises the failure of the call that started it, so what the runner classifies is a single kind. See
"Route Fallback" below.

## Routing

Routing is configuration-driven, and the triage configuration file is the source of truth. Named
routes under `Routes` in `config/incidentcompass.config.json` carry the kind, provider ID, model,
temperature and output/context ceilings. The shipped routes are `analysis-chat`, `report-chat` and
`memory-embed`. Roles, the orchestrator and the `memory_search` tool select a route by its ID, so a
route is the only thing that decides which model a triage call uses.

There is no host-level model setting beside the route. `ModelGateway` and `Embeddings` configure the
provider, the request ceilings and the transport; the model name for a call comes from the route the
caller resolved and from nowhere else.

### Providers

Entries under `Providers` in the triage configuration are real bindings, not labels. A route's
`ProviderId` selects one, and the entry decides which endpoint answers that route's calls and which
credential the request presents. Chat completions and embeddings resolve through the same provider
table, so a route naming a provider and an embedding route naming a different one reach different
endpoints.

A provider entry carries two fields beyond its `Kind`:

- `Endpoint`: the absolute base URL for that provider. It is validated at load and must be `http`
  or `https`; whether plaintext HTTP is actually permitted is still the host-wide loopback decision
  described below.
- `ApiKeySecretRef`: the **name** of an environment variable holding that provider's credential,
  never the credential. See `docs/security-model.md`, "Model Provider Credentials".

What is per-provider and what is host-wide:

| Setting | Scope |
| --- | --- |
| Endpoint base URL | per provider, falling back to the host-wide `BaseUrl` |
| API credential | per provider, falling back to the host-wide `ApiKey` |
| `ChatCompletionsPath` / `EmbeddingsPath` | host-wide |
| `TimeoutSeconds`, retry counts and delays | host-wide |
| `ReasoningModes` | host-wide, keyed by provider id |
| `AllowInsecureHttpForLoopback` | host-wide |
| `Organization` | host-wide |

The fallback has one rule, and it is what keeps every existing configuration working unchanged:

> `IncidentCompass:ModelGateway:OpenAiCompatible` and `IncidentCompass:Embeddings:OpenAiCompatible`
> are the **default provider profile**. A configuration that declares exactly one provider keeps
> using them; that provider's `Endpoint` and `ApiKeySecretRef` override the default where they
> resolve, and the default fills whatever they leave. A configuration that declares more than one
> provider gets no default at all: every `OpenAICompatible` entry must name its own `Endpoint` and
> its own `ApiKeySecretRef`, and each named variable must be set.

The line is drawn at the provider count rather than per entry because the failure a default would
cause in a multi-provider configuration is not a missing call. It is the first provider's credential
arriving at the second provider's endpoint. Refusing to start is the only safe answer to that, so a
multi-provider configuration missing an endpoint or a resolvable credential fails while the host is
starting, not at the first model call. A `Mock` provider entry is exempt, because it has no endpoint
to reach and no credential to present.

The provider table is read from the currently loaded configuration rather than from the snapshot a
running job is pinned to. A job's route - its model, its ceilings, its reasoning preference - stays
pinned, because that is what makes a report reproducible. Where a call is sent and what it
authenticates with is operational rather than behavioural, and rotating a key or moving an endpoint
must not require draining every in-flight job first.

A direct `IAiModelClient` or `IEmbeddingClient` caller that supplies no `ProviderId` reaches the
host-wide profile without the triage configuration being read at all.

### Route Fallback

A chat route may name another chat route as its `FallbackRouteId`. When the provider fails one of
that route's model calls in a way a different provider could plausibly answer, the same call is
retried once on the fallback route, which reaches that route's own provider, model and ceilings.
Absent means no fail-over, which is what every shipped route does.

```json
"report-chat": {
  "Kind": "Chat", "ProviderId": "local-oai", "Model": "local-model",
  "FallbackRouteId": "report-chat-backup"
}
```

The declaration is validated while the host starts, not at the first failure: the named route must
exist, must be a chat route and must not be the route itself, and a fallback on an embedding route is
rejected because nothing on the embedding path executes one. A route naming a provider that is not in
the `Providers` table already fails load in its own right, so a fallback cannot resolve to one.

**Which failures are retried elsewhere.** Only `Unavailable` and `GenerationTimeout`. The first is the
case the feature exists for: the provider positively did not accept the request - 429, 503, a refused
connection, a name that did not resolve, a failed TLS handshake - so nothing was generated and a
different endpoint is the one thing that can plausibly answer. The second is the provider owning a
deadline and not answering inside it, which a different provider, or the smaller model a fallback
route usually names, plausibly does inside what is left of the attempt.

Nothing else is. `RejectedRequest` describes a request that the same request, sent again, reproduces;
`OutputLimitReached` is a completion that ran into its ceiling; `InvalidResponse` covers an empty
completion and a malformed tool call as well as an unparsable body. Those are the model's own answer
being wrong, and a second provider is the same money spent twice for the same outcome.
`AmbiguousInterruption` means the request may already have been accepted and generated, which is
exactly why it dead-letters rather than being replayed; `Unknown` is unclassified by definition; and
`TransportFailure` is not produced on the chat path at all.

**What fail-over does not change.**

- *The deadline.* Both calls share the single cancellation the attempt-budget gate created for the
  first one, so `MaxWallClockSeconds` bounds the pair and no configured bound doubles. The provider's
  own per-HTTP-attempt `TimeoutSeconds` still bounds each call, and a fallback that runs into the
  attempt deadline ends as a budget exhaustion, because the attempt really did run out of time. A
  call that is already cancelled is not failed over at all.
- *Admission.* The attempt-budget gate admits the call once, before the first attempt at it: a
  fail-over is the same logical call reaching a second endpoint, not a new request asking for
  permission. One consequence is worth stating plainly: the prompt was measured against the
  declaring route's `ContextWindowTokens`, and the fallback route's own value is not re-checked, so
  a fallback naming a materially smaller context window is a configuration mistake the load
  validator does not catch.
- *The accounting.* Both calls are charged. The failed call's `ModelCall` row and its `BudgetEvent`
  charge are made durable before the second call is allowed to spend anything, and nothing refunds,
  exempts or hides them.
- *The disposition.* One hop only: a fallback does not itself fail over, even if the route it names
  declares one. A fallback that fails at the provider raises the primary's failure, so the job runner
  still reads exactly one failure kind and applies exactly one disposition from the table above.
- *Claim backpressure.* Only a call on the route's own provider clears the process-local pause. A
  fallback answering is evidence about the fallback's provider and none about the one that failed, so
  claiming stays paused rather than resuming against a provider that is still down.

**What the report says.** A report published from an attempt in which a call was answered by a
fallback carries a backend-owned limitation stating that at least one model call failed on its
configured route and was answered by that route's fallback. It follows the withheld-evidence and
read-only-context markers: derived at publication from the ledger, owned in both directions, and
never asked of the model, which has no way to know which route dispatched it. The model provenance on
the report names the fallback route and model that answered; it does not, on its own, say that
anything failed first, which is what the sentence adds. See `docs/observability.md`.

### Route Reasoning Preference

A chat route may set its optional `Reasoning` value to `off`, `low`, `medium` or `high`. When the
value is absent, the client sends no reasoning-specific request field, so existing shipped routes
retain their current request shape.

For an OpenAI-compatible host, `ModelGateway:OpenAiCompatible:ReasoningModes` is an explicit map
from the route's logical provider ID to one of `Disabled`, `ReasoningEffort` or
`ChatTemplateKwargs`. The client never infers a reasoning protocol from a model name. A missing
provider-ID mapping, or a `Disabled` mapping, sends no reasoning-specific request field.

With `ReasoningEffort`, the route values map to the lowercase `reasoning_effort` values `none`,
`low`, `medium` and `high` respectively. With `ChatTemplateKwargs`, the client sends
`chat_template_kwargs.enable_thinking`: `off` becomes `false`; `low`, `medium` and `high` become
`true`. That mode intentionally loses intensity, and a local server may ignore the field.

`MaxOutputTokens` retains its existing semantics. On most servers it still limits the combined
reasoning and final-answer output, rather than reserving a separate final-answer allowance.

### Shipped Local-Safe Profile

The shipped profile pairs the chat provider's 300-second `TimeoutSeconds` with an orchestrator
`MaxWallClockSeconds` of 600. Both `analysis-chat` and `report-chat` allow `MaxOutputTokens: 8000`
and retain `ContextWindowTokens: 8192`; the orchestrator retains `MaxTokens: 200000` and
`MaxReprompts: 2`. These settings form one local-safe profile for slower local generation. They are
ceilings, not target token consumption or expected latency, and each call is still canceled when
the investigation's remaining wall-clock budget expires.

`ContextWindowTokens` only limits the backend's prompt-size estimate for a route. It does not
subtract from or reserve room inside the separate 8000-token provider output ceiling. The embedding
adapter retains its separate 30-second default timeout.

The separation between these deadlines preserves two intentionally different dispositions. A
stalled call that reaches its provider-owned deadline first fails as
`provider_generation_timeout`, consumes the current job attempt and remains retryable while job
attempts remain. If the investigation's remaining wall clock expires first, the bounded-run failure
dead-letters immediately without spending another job attempt. Bringing the provider timeout too
close to the investigation budget would make a later call hit the wall-clock path before its own
timeout. Keeping the per-call ceiling noticeably lower leaves both outcomes meaningfully reachable;
it does not prevent the remaining wall clock from canceling a call that starts late.

Cloud operators can tighten the host timeout and triage route/budget overrides for their measured
provider latency and cost requirements. Until streaming stall detection is available, the larger
timeouts also mean a stalled generation can take longer to surface as a failure.

## Investigation Budget Events

Investigation model calls write compact redacted `ModelCall` metadata. Each row includes a unique
call id, `success` or `failed` outcome and a normalized error code when failed. Its `payload_ref` is
`model-call:<call-id>`; the matching `BudgetEvent`, when present, carries the same reference. The
shared reference lets failure persistence deduplicate accounting for one call while holding the job
lock.

On success, the `ModelCall` and token-charge `BudgetEvent` are appended as one ledger transaction.
Provider usage is preferred and a compact backend estimate is used when successful usage is absent
or incomplete. On failure, returned provider usage is retained without estimation: known usage,
including an explicit zero, produces the matching charge, while unknown usage leaves token fields
null and does not invent a charge. Failure accounting, the fenced job disposition and any terminal
fault update commit or roll back together. If the lease owner is stale, its call accounting remains
audit-visible, but the fenced update does not alter the current job or fault.

Budget decisions sum `BudgetEvent.tokens_delta` and `BudgetEvent.workers_delta`, never rendered
prompts, full provider responses, `ModelCall` rows or `BudgetEvent` rationale text. `MaxTokens`
prevents starting a call once the current-attempt token budget is already reached; one-call overshoot
is possible and is recorded. `MaxWallClockSeconds` is checked between calls and passed into model
calls through cancellation. The shipped ceilings above do not change these accounting or
cancellation rules.
