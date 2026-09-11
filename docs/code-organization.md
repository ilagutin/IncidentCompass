# Code Organization and Self-Documenting Rules

These rules keep the production codebase easy to audit, refactor and hand over to another team. Treat them as engineering guardrails, not formatting ceremony. An exception is acceptable only when it is explicit, local and easier to defend than the split it avoids.

The gate covers both `src/` and `tests/`, with a looser size budget for test code: a production file
stays under 400 lines, a test file under 800. The responsibility, naming and design rules below are
production rules; the gate applies its nested-private-type and status-string checks to `src/` only,
because test code legitimately declares private nested test doubles and asserts on the persisted
status representation.

## Size Guardrails

- A production class should stay under 400 physical lines. If it exceeds that limit, the code should be split unless the file is a simple composition root, generated code, a framework-required shape, or another clearly justified exception.
- A test file should stay under 800 physical lines. Test classes carry fixtures, doubles and setup, so they get a looser budget than production code; past that limit, split them by the subject under test rather than by line count and move shared setup into a named support type.
- A method should fit in one readable workflow step. Long methods should be split by intent, for example validation, state loading, policy decision, side effect, persistence and response mapping.
- A large handler is a design smell. A handler should orchestrate a use case; domain rules, provider-specific work, rendering, parsing, persistence details and reusable policies should live behind named collaborators.
- Do not hide complexity by extracting vague helpers. Prefer small methods and types named after the business or workflow concept they represent.

## Responsibility Boundaries

- A class should have one reason to change. If a file changes for unrelated endpoints, storage details, validation rules and response shaping, it is carrying too many responsibilities.
- API endpoint classes should stay thin. They may group closely related route mapping, but request handling, orchestration and business decisions belong in application use cases.
- Application handlers should coordinate ports and policies. They should not become a substitute for the pipeline, domain model or infrastructure adapters.
- Infrastructure adapters may contain provider or persistence details, but those details should not leak into application handlers, controllers or domain code.

## Endpoint Organization

- Use one mapper file per route group. A top-level API version mapper should compose feature mappers such as health and users.
- Endpoint lambdas should stay transport-focused: bind HTTP input, dispatch an application request and map the application result to HTTP.
- Do not let endpoint files own workflow policy, provider error taxonomy or reusable validation rules. Put those behind application policies or shared API error mappers.
- Request and response DTOs may live near endpoint mappers, but split them into separate files when the mapper stops reading as route composition.

## Validation Placement

- API validation should cover transport shape only, such as missing multipart content, malformed route values or invalid JSON shape.
- Business input validation belongs in the application use case, preferably in `Validator.cs` for non-trivial commands and queries.
- Shared request limits and model-call validation should live in named policies or validators, not inline in endpoint lambdas or large handlers.
- Do not duplicate the same validation rule across API, handler and infrastructure. Choose the earliest reliable layer and keep later checks defensive.

## File and Type Boundaries

- Use one entity per file: one class, record, struct, enum or interface.
- Avoid private nested entities. Keep one only when a framework or compiler shape makes extraction worse, and document that exception in review.
- Do not bundle command, response, validator, options, result and helper records into one convenience file. Split them so review diffs and ownership remain obvious.
- Test fixtures, builders and test scenario files are exempt from this gate.

## Application Pipeline Layout

Organize use cases by feature and action. Let the folder carry the context and keep file names simple:

```text
IncidentCompass.Application/
  Core/
    Dispatching/
    Security/
    Configuration/
    Health/
    Users/
      GetCurrent/
        Query.cs
        Handler.cs
        Response.cs
    ModelClients/
    ModelGateway/
    Embeddings/

  Governance/
    ActionApprovals/
    Ledger/
    Tools/
    Validation/
```

`Core/`, `Governance/`, `Intake/`, `Investigation/`, `Memory/`, `Notifications/`, `Observability/`,
`Remediation/`, `SourceContext/` and `Tickets/` are the current folders, and `ArchitectureTests`
enforces exactly that list. `Governance/` contains the common worker-tool contract, validation primitives, triage
ledger ports and post-report action approval contracts/use cases, including the deterministic approved
action dispatcher. PostgreSQL action approval, provenance, claim, recovery and terminal-transition
implementations stay under `Infrastructure/Governance/ActionApprovals/`.
The single live tool rule engine and the immediate/action capability contracts live under
`Governance/Tools/`; investigation-only execution orchestration stays under `Investigation/Jobs/`.
`Memory/` contains memory_search contracts, seed records, retrieval orchestration and the corpus
generation identity plus the pure evaluator that decides what a corpus is relative to the configured
embedding route. Seed scanning, the synchronization pass, the operator `memory status` and
`memory rebuild` commands and the PostgreSQL generation writer stay under `Infrastructure/Memory/`.
Payload retention is split the same way the data is: `Intake/Retention/` owns compaction of raw
signal payloads and `Investigation/Retention/` owns reaping of non-current-attempt artifacts, each a
persistence port plus the bounded callable operation that turns the shared `RetentionOptions` window
into a cutoff. The single-statement SQL, its exclusion list and the index that bounds each scan stay
in the matching `Infrastructure/` folder.
`Observability/CostRollup/` contains the tenant-scoped read request, validator, response and
persistence port. ModelCall JSON parsing, effective-price ambiguity handling and PostgreSQL query
details stay under `Infrastructure/Observability/`; API endpoints remain transport-only.
`Remediation/` contains the post-report remediation pass: the disposable-workspace port, the answer
extractor that decides whether a model reply is a unified diff, the prompt builder, the bounded runner
and the durable diff record with its persistence port. Materialization, tree identity, diff parsing and
diff application stay in `Infrastructure/SourceContext/`, where the read boundary's own primitives
already live, and `Infrastructure/Remediation/` holds only the adapter that composes them and the
PostgreSQL writer. The runner deliberately reaches `Investigation/Jobs/`'s bounded model caller rather
than owning a call path, so admission, the provider deadline, ledger accounting and route fail-over stay
in one place; that is the one cross-folder dependency the feature has and it is the point of it.
`SourceContext/` contains the provider-neutral read port, bounded signal frame extraction and tool;
filesystem roots, canonicalization and file reads stay in Infrastructure. Report-level context
outcome contracts and backend limitation policy live under `Investigation/Reports/Context/`.
`Tickets/` contains the provider-neutral search, cited-update-evidence and action-history ports,
bounded signal-field extraction, read worker tool, ticket-create and ticket-update descriptors,
eligibility checks and canonical payload rules. Provider query syntax, HTTP transport, credentials,
response parsing, ranking, durable evidence/history queries and write adapters stay under
`Infrastructure/Tickets/`. Ticket writes must enter through the governed post-report proposal,
approval and dispatch path, never a worker role tool.

The Worker keeps triage-job and approved-action scheduling in separate pump/task-set types. The action
pump owns only bounded polling, task observation and shutdown draining; current-policy checks, exact
payload dispatch and terminal workflow decisions remain in Application, while database fencing remains
in Infrastructure. Retention follows the same split: the Worker owns when a pass happens - the
schedule options, their validator, the pump that runs one bounded pass of each operation in one scope,
and the hosted service around it - while what a pass does stays in the Application operations. A
schedule is a property of a host, so it is bound from the Worker's own configuration section rather
than from the shared `RetentionOptions` that both operations read.

Use `Query.cs` instead of `Command.cs` when the use case is read-only. Avoid repeating the full folder context in file names, such as `GetCurrentUserQuery.cs`, when `Users/GetCurrent/Query.cs` already communicates the intent.

If the existing name does not fit the action folder, treat that as a naming problem first. Either rename the action or split the use case until the folder and type names describe one behavior.

For non-trivial commands and queries, add `Validator.cs` beside the request and handler. `Validator.cs` contains FluentValidation rules for request shape and early input policy only. When validation also needs to produce a normalized value object for the handler, add `Normalizer.cs` beside it and keep trimming, defaults and enum parsing there. Tiny query objects may omit a validator only when there is no meaningful input rule.

## Pipeline Behaviors

Application requests run through the internal dispatcher pipeline before the handler. Behaviors are registered in `Setup.cs` in outer-to-inner order.

- `DispatchLoggingBehavior` is outermost and records request lifecycle telemetry, including validation failures.
- `RequestValidationBehavior` runs all FluentValidation validators for the request type and throws `RequestValidationException` when rule failures exist.

Add a new behavior only for cross-cutting workflow concerns that should apply consistently across many request types. Keep feature-specific policy in the feature folder instead of hiding it in a global behavior.

Behavior boundaries:

- A behavior is an application concern, not a replacement for HTTP middleware. HTTP-only concerns
  stay in `IncidentCompass.Api`.
- A behavior may open a persistence transaction, but the transaction itself is implemented by
  Infrastructure.
- Model-call telemetry stays on the Worker investigation path through durable `ModelCall` ledger
  events rather than a global behavior.
- The dispatcher is a small internal type. MediatR is not a required dependency; see
  `docs/trade-offs.md` for the replacement seam if a team prefers it.
- Out of scope for this pipeline: event sourcing, separate read/write databases and a dependency on
  a commercial mediator package.

## Handler Shape

A healthy handler reads as a short ordered workflow:

1. Validate use-case-specific invariants that are not already covered by a validator.
2. Load required state through application ports.
3. Apply policies and domain decisions.
4. Call external ports with cancellation and clear failure semantics.
5. Persist durable state intentionally.
6. Map the application result.

When a workflow crosses storage, persistence, provider calls, leases, retries, cleanup or authorization, document and test the important partial failure states. Do not return success after a required durable side effect failed unless there is a documented recovery invariant and a test proving it.

If a use case crosses storage, database, provider calls, retries, leases, cleanup or cancellation, the handler should not own all failure-state logic directly. Extract a named coordinator, policy or workflow service that makes those state transitions explicit.

## Provider and Infrastructure DTOs

- Keep external-provider DTOs inside Infrastructure adapter folders, but not as nested records inside the client class once there is more than one or two DTOs.
- Provider request/response DTOs should be named after the external protocol, not after application contracts.
- Application and Domain types should not expose provider DTOs or provider-specific error shapes.
- Normalize provider errors at the adapter boundary before they reach application handlers.

## Infrastructure Error Boundary

Infrastructure adapters must catch infrastructure-specific exceptions at the port boundary and rethrow as an Application-layer exception declared in the port contract.

Examples:

- `PostgresException`, `NpgsqlException` and `TimeoutException` from Npgsql calls are caught in the repository adapter and rethrown as `ProviderException` or a concrete application exception.
- `HttpRequestException`, `TaskCanceledException` and `JsonException` from `HttpClient` calls are caught in the provider client and rethrown as `AiModelException` or `EmbeddingClientException`.

Infrastructure exception types must not appear in `ApiExceptionHandler` switch cases except for bootstrap-time configuration errors, such as `PostgresConnectionConfigurationException` mapped to 500 during startup.

Rationale: the API exception handler depends only on Application and Domain exception contracts. Adding a new Infrastructure adapter must not require changes in the API layer.

## Async Conventions

- Do not call `ConfigureAwait(false)`. Both hosts are application hosts (ASP.NET Core and the generic host) with no synchronization context, and this solution ships no library that a caller could host differently, so the call changes nothing and only makes await sites read inconsistently.
- Awaiting a task purely to observe it, rather than to use its result, needs a comment saying so. An abandoned faulted task surfaces later as a process-level `UnobservedTaskException`, which is not obvious from the empty `catch` alone.

## Workflow States and Error Mapping

- Do not represent internal workflow states as scattered strings. Use enums or value objects in Domain/Application and convert to strings only at API or persistence boundaries.
- Public error codes and HTTP status mapping should be centralized in API error mappers or application error contracts.
- Avoid local `switch` blocks that independently translate the same provider, retrieval, validation or tool states in multiple endpoints or handlers.
- When a string is required for persistence or public compatibility, define constants or a typed mapping close to the boundary.

## Dependency Registration

- Each `src/*` project exposes a single `Setup.cs` at its root as the DI entry point. The class is named `Setup` and contains the public `AddX` extension method (`AddApplication`, `AddInfrastructure`, etc.). `Setup.cs` doubles as the assembly marker - prefer `typeof(Setup).Assembly` over arbitrary types for embedded-resource or assembly-scanning operations.
- Feature-level registration delegates live next to the feature as `<Feature>Setup.cs` (for example `HealthSetup.cs`, `UsersSetup.cs`). The root `Setup.cs` composes these via feature-named extension methods such as `AddHealthCore` or `AddUsersCore`.
- DI modules should register dependencies only; they should not contain business validation or runtime decision logic.
- `AddApplication` binds deferred "not configured" placeholders for ports that only an infrastructure adapter can implement, so partial graphs stay buildable. Host entry points (`AddApi`, `AddWorker`) end by calling `ValidateApplicationWiring()`, which fails composition when a placeholder is still bound. Add the check to any new host entry point rather than letting the placeholder throw at first use.
- Configuration is read at composition time and passed to typed options. `IConfiguration` is not registered as an application service by `AddInfrastructure`; connection strings reach adapters through `PostgresConnectionOptions`.

## Self-Documenting Code

- Prefer explicit names over comments that explain what the code already says.
- Add short comments only for non-obvious concurrency, retry, transaction, security or recovery decisions.
- Remove duplication when it repeats workflow decisions or business rules. Local repetition in trivial mapping code is less harmful than a generic abstraction with unclear ownership.
- Avoid magic strings and scattered configuration keys when typed options, constants or policies already exist.
- Keep tests focused on behavior. Test setup should make the scenario clearer, not bury the reason for the assertion.

## Review Checklist

Before merging a change, check:

- Does any production class exceed 400 lines without a clear reason?
- Does any method mix unrelated workflow stages?
- Does each file contain one entity?
- Are command/query, handler, validator and response types placed under a feature/action folder?
- Can each handler be explained as orchestration rather than implementation detail?
- Are endpoint mappers split by route group and limited to HTTP concerns?
- Does non-trivial business validation live in validators or named policies?
- Are provider DTOs isolated from application contracts and split out of large clients?
- Are infrastructure exceptions normalized at the port boundary?
- Are internal workflow states typed instead of stringly-typed?
- Is public error mapping centralized?
- Is dependency registration still modular enough to review?
- Is duplicate behavior extracted behind a meaningful concept?
- Are partial failure states covered for non-transactional or external side effects?
