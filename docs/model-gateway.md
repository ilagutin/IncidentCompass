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

- OpenAI-compatible client;
- mock/fake client for tests and explicit mock-only checks.

Possible future adapters:

- Azure OpenAI;
- local OpenAI-compatible endpoints;
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

Embedding requests retain their idempotent retry boundary: HTTP 408, 429 and every 5xx response,
including 501/505, plus configured timeouts and transport failures are retried up to the embedding
retry limit. Terminal embedding failures are then cause-classified. HTTP 429 and retryable 5xx
responses other than 501/505 and positively safe pre-dispatch transport failures are `Unavailable`;
HTTP 408 and an exhausted configured timeout are `GenerationTimeout`; HTTP 501/505 and
configuration errors are `RejectedRequest`; other exhausted transport failures, including
connection reset and response-ended failures, are `TransportFailure` with `transport_error`; and
invalid JSON or an empty vector is `InvalidResponse`. Caller cancellation propagates unchanged.

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
  the chat-generation `MaxRetryDelaySeconds`. That ceiling defaults to 5 seconds, is configurable
  from 1 through 3600 seconds and also caps the exponential fallback used when the header is absent
  or no longer in the future. HTTP 501/505 responses are not retried for generation.
- Token usage is captured when returned by the provider. Budget accounting uses provider usage when present and a compact backend estimate otherwise, recording the source in the ledger.
- Provider errors are normalized into the application-owned failure kinds `Unavailable`,
  `RejectedRequest`, `GenerationTimeout`, `OutputLimitReached`, `AmbiguousInterruption`,
  `TransportFailure`, `InvalidResponse` and `Unknown`. The base provider-exception type alone does
  not imply an outage.
  Model-provider outages are retried as an explicit `provider_unavailable` delayed job state. After
  the configured consecutive-failure threshold, each Worker process pauses new claims for
  `IncidentCompass:ProviderResilience:BackpressureSeconds`; a successful model call clears that
  local pause. This is in-process backpressure, not cross-host coordination, and fallback routes
  remain later scope.
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

## Routing

Routing is configuration-driven:

- default model;
- strong model;
- cheap model;
- evaluation model.

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
