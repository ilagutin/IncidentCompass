# Agent Operating Contract

Treat this file as the operating contract for future coding agents and human maintainers.

## Project Intent

`IncidentCompass` is a governed incident-triage agent backend. It ingests a signal, an orchestrator
delegates to scoped workers under policy/audit/budget rails, and produces a grounded report. It was
bootstrapped from an upstream starter-kit reference repo (see `README.md` for the full relationship).

This is a reference-quality implementation, not a production system and not a stable framework. Keep
changes practical, reviewable and aligned with the public documentation.

## First Read

Before making non-trivial changes, read the relevant public docs:

- `README.md` for scope, quickstart and release framing.
- `docs/architecture.md` for project boundaries.
- `docs/code-organization.md` for maintainability rules.
- `docs/security-model.md` for auth and logging safety.
- `docs/model-gateway.md` for provider abstraction rules.
- `docs/observability.md` for live telemetry and privacy boundaries.
- `docs/trade-offs.md` for documented compromises.

## Architecture Contract

- Keep the solution a layered monolith (a single `Application` project with feature folders; layer
  boundaries are by convention + `ArchitectureTests`, not enforced module assemblies).
- `Domain` must not depend on `Application`, `Infrastructure`, `Api`, `Worker`, provider SDKs or persistence libraries.
- `Application` is a single project with populated feature folders: `Core/`, `Governance/`,
  `Intake/`, `Investigation/`, `Memory/`, `Notifications/`, `Observability/`, `SourceContext/` and
  `Tickets/`. `ArchitectureTests` enforces exactly this list; adding a folder means changing both.
- `Core/` holds the dispatcher, identity/correlation, model/embedding gateway abstractions and shared options.
- `Intake/` holds source normalization, redaction, fingerprinting, fault grouping, triage-job creation and grounded intake artifacts.
  `Intake/Configuration/` also holds the deserialized triage-configuration model for the whole system:
  provider, route, role, tool, rule and orchestrator-budget settings. Every feature that needs triage
  configuration reads it from there. That location is historical, not a claim that those settings are
  intake-specific.
- `Investigation/` holds Worker job orchestration, config rehydration, bounded model calls, delegation,
  worker-tool execution and grounded report publication contracts.
- `Memory/` holds incident-memory contracts and the governed `memory_search` tool.
- `Governance/` holds live triage-ledger contracts, worker-tool contracts and validation primitives.
  `ToolRuleEngine` is the single tool-policy decision path shared by immediate Worker reads and
  backend-owned post-report action proposals.
- `Notifications/`, `SourceContext/` and `Tickets/` are top-level feature folders that own different
  pieces: `Notifications/` holds route selection, the Worker-owned workflow and the non-secret
  `telegram_notify` action descriptor, with no port interface and no worker tool; `SourceContext/`
  holds the `ISourceContextLookup` port and the governed `source_lookup` worker tool, with no action
  descriptor; `Tickets/` holds provider-neutral ports, the governed `ticket_search` worker tool and
  the non-secret `ticket_create` action descriptor. They sit beside `Governance/` rather than inside
  it: `Governance/Tools/` owns the shared tool contract and the single rule engine, not any
  feature's own tool or descriptor.
- `Observability/` holds the tenant-scoped model-cost rollup read model and its persistence port.
- `Infrastructure` implements PostgreSQL persistence, configuration loading, model/embedding clients,
  incident memory and other Application ports.
- Live model observability uses structured application logs plus durable `ModelCall` and `BudgetEvent`
  entries in the triage ledger. Full rendered prompt/body logging remains disabled.
- `Api` maps HTTP input/output, OpenAPI metadata and foreground user context only.
- `Worker` runs the database-backed claim loop, governed investigation processing and the periodic
  data-retention pass, composing only `Application` and `Infrastructure`. It owns when a scheduled
  pass happens; `Application` owns what the pass does.
- `Tester` is an HTTP-only demo and evaluation driver with no project references. It speaks to the API
  as a black box, so it deliberately declares its own copies of Domain and Application concepts, such
  as `EvaluationJobStatus` and `EvaluationModelCallMetadata`, instead of sharing types.
- Hosts compose four registrations: `AddApplication` + `AddInfrastructure` + `AddPostgresMigrations`
  (+ `AddApi`/`AddWorker`) rather than per-feature registration.
- Provider-specific DTOs, HTTP details, SQL details and SDK concepts must not leak into Application or Domain contracts.

## Current Design Decisions

- Runtime: .NET 10 LTS.
- API surface: `/api/v1/...`.
- Architecture: Clean Architecture with simple domain records and a single Application project organized by feature folder.
- CQRS: CQRS-lite by feature/action folder, not separate read/write systems.
- Pipeline: lightweight internal dispatcher instead of a required MediatR dependency.
- Validation: FluentValidation runs in the dispatcher pipeline; feature normalizers build handler-ready value objects when needed.
- Persistence: raw Npgsql for explicit PostgreSQL behavior.
- Auth: foreground `IUserContext` for API callers, `IBackgroundUserContext`
  for Worker/system jobs, and demo header auth only for local/sample use.
- Providers: OpenAI-compatible providers are the normal local/demo runtime path; deterministic mock providers are for automated tests and explicit mock-only checks.
- Governance: the live Worker path validates role grants and configured rules before backend tool execution, records decisions in the triage ledger and fails closed through a single rule path.
- Intake fingerprinting: a strong fingerprint requires both a real non-`unknown` service name and structured `errorType`. A user/manual report with only an operator-entered `serviceName` remains weak and opens its own fault.

## Safety Rules

- The LLM is not a security boundary.
- Full rendered prompt/body logging must stay disabled by default.
- Do not log document text, rendered prompts, provider credentials, full connection strings, API keys or embedding vectors.
- Automated tests must not call real model or embedding providers by default.
- Tool execution must be deterministic backend behavior. The model may propose actions; backend validation, policy and approval state decide execution.
- Unknown, forbidden or invalid tool calls must fail closed and be audit-visible.

## Reliability Rules

- Treat Application ports as contracts. Do not rely on one adapter's incidental behavior to make a workflow safe.
- Normalize infrastructure-specific exceptions at the Application port boundary; follow `docs/code-organization.md` Infrastructure Error Boundary.
- For workflows crossing storage, persistence, workers, retries, external providers or auth, reason about partial failure states before changing behavior.
- Do not return public success after a required durable side effect failed unless a documented recovery invariant and tests prove the state is safe.
- Worker provider calls must be cancelable or explicitly observed when shutdown or cancellation occurs.
- PostgreSQL/Testcontainers coverage should run for persistence and schema changes. If Docker-backed tests are skipped locally, call out the residual risk.

## Code Organization

- Follow `docs/code-organization.md`.
- Keep production classes under 400 physical lines unless a local exception is clearly easier to defend than a split.
- Keep one public/internal type per file: class, record, struct, enum or interface.
- Use feature/action folders for Application use cases: `Command.cs`, `Query.cs`, `Handler.cs`, `Validator.cs`, `Response.cs`.
- Keep handlers as orchestration. Put parsing, policy, persistence detail and provider detail behind named collaborators.
- Keep endpoint handlers transport-focused: bind input, dispatch Application request, map response.
- Put business validation in validators or named policies, not endpoint lambdas.
- Centralize public error-code and HTTP-status mapping.
- Prefer typed options and constants over scattered configuration strings.
- Add abstractions only when they protect a real boundary or remove meaningful duplication.
- Use explicit names that describe the workflow concept, not vague helpers.

## Documentation Rules

- Keep public docs synchronized with behavior changes.
- If a change affects setup, security posture, provider behavior, request/response shape or persistence, update the matching file under `docs/` and the README when needed.
- Preserve honest framing: this is reference-quality software, not a production deployment.
- Document intentional trade-offs instead of hiding them.
- Keep release notes factual: implemented features, defaults, known non-goals and verification scope.

## Verification

Use the smallest reliable check first, then broaden when the changed surface warrants it.

Common commands:

```powershell
dotnet restore IncidentCompass.slnx
dotnet build IncidentCompass.slnx
dotnet test --solution IncidentCompass.slnx
dotnet format IncidentCompass.slnx --verify-no-changes --verbosity minimal
powershell -ExecutionPolicy Bypass -File scripts\package-vulnerability-gate.ps1
powershell -ExecutionPolicy Bypass -File scripts\code-organization-gate.ps1
powershell -ExecutionPolicy Bypass -File scripts\internal-reference-gate.ps1
```

After changing a package version in `Directory.Packages.props`, regenerate every lock file with
`dotnet restore IncidentCompass.slnx --force-evaluate` and commit the files it changes. Central
package management makes one bump change the lock file of every project that reaches the package,
and CI restores with `--locked-mode`; see `docs/versioning.md`, "Dependency Lock Files".

For persistence-sensitive changes:

```powershell
$env:INCIDENTCOMPASS_REQUIRE_DOCKER_TESTS = "true"
dotnet test --project tests\IncidentCompass.IntegrationTests\IncidentCompass.IntegrationTests.csproj
```

## Contribution And Release Flow

- `main` is protected. All changes land through a pull request; never push directly to `main`.
- An automated agent may push a feature or release branch and open a pull request only after explicit
  maintainer approval. It must not push to `main` and must not push tags.
- Before opening a pull request, run the local verification commands above and stage only intended
  files with explicit paths; never `git add -A`.
- Keep commits atomic and green: one logical change per commit, each passing the gate.
- For a public release PR, update `VERSION` with SemVer without a leading `v`, update `CHANGELOG.md`,
  and add the matching `docs/release-notes-v<version>.md` file.
- Releases are automatic after a release PR that changes `VERSION` is merged to `main`: the
  `publish-release` workflow runs on that `main` push, reads `VERSION`, validates the matching
  release-notes file, reruns build/format/code-organization/vulnerability/unit-test gates, tags the
  current `main` HEAD and publishes the GitHub release. Full integration coverage is enforced before
  merge by PR/main CI. Non-release PRs must not change `VERSION`.
- Normal releases do not require `Run workflow`. `workflow_dispatch` and direct tag pushes are recovery
  paths only. Agents must not push release tags.

## When Unsure

Prefer the existing architecture and documented trade-offs. Ask only when a decision changes architecture, versioning, security posture or public behavior. For local implementation details, make the conservative choice that keeps the project understandable.
