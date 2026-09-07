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

## Requirements

- Model name is configurable.
- Timeout is configurable.
- Retry policy is supported or explicitly planned.
- Token usage is captured when returned by the provider. Budget accounting uses provider usage when present and a compact backend estimate otherwise, recording the source in the ledger.
- Provider errors are normalized into application-level error types. Model-provider outages are retried as an explicit `provider_unavailable` delayed job state. After the configured consecutive-failure threshold, each Worker process pauses new claims for `IncidentCompass:ProviderResilience:BackpressureSeconds`; a successful model call clears that local pause. This is in-process backpressure, not cross-host coordination, and fallback routes remain later scope.
- Request/response objects carry correlation IDs.
- OpenAI-compatible chat completions include one `Idempotency-Key` header per
  high-level model request and reuse it across retry attempts.
- Tests use mock clients by default.
- OpenAI-compatible provider URLs use HTTPS by default; insecure HTTP is allowed only for explicit loopback development configuration.

## Routing

Routing is configuration-driven:

- default model;
- strong model;
- cheap model;
- evaluation model.

## Investigation Budget Events

Investigation model calls write compact redacted `ModelCall` metadata and a `BudgetEvent` charge. Budget decisions sum `BudgetEvent.tokens_delta` and `BudgetEvent.workers_delta`, never rendered prompts, full provider responses, `ModelCall` rows or `BudgetEvent` rationale text. `MaxTokens` prevents starting a call once the current-attempt token budget is already reached; one-call overshoot is possible and is recorded. `MaxWallClockSeconds` is checked between calls and passed into model calls through cancellation.
