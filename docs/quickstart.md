# Quickstart

This guide runs IncidentCompass against OpenAI-compatible model and embedding endpoints. Mock
providers are reserved for automated tests and explicit `-Mock` checks.

## Prerequisites

- .NET 10 SDK.
- Docker, for PostgreSQL and the compose demo.
- An OpenAI-compatible chat completions endpoint and embeddings endpoint. A local server on port 1234
  works with the checked-in defaults if it accepts the configured model names.
- PowerShell examples below assume Windows, but the same dotnet and docker compose commands work
  cross-platform with shell syntax changes.

## One-Command Demo

Configure local model names first when your provider requires exact ids:

~~~powershell
Copy-Item .env.example .env
# Edit .env:
# INCIDENTCOMPASS_LLM_MODEL=<chat-model-id>
# INCIDENTCOMPASS_EMBEDDINGS_MODEL=<embedding-model-id>
~~~

Run the compose demo:

~~~powershell
powershell -ExecutionPolicy Bypass -File scripts/demo.ps1
~~~

The script builds the API, Worker and Tester images, starts PostgreSQL/API/Worker, waits for
the API health endpoint on its resolved host port, then runs the Tester against the local scenarios. Compose
health checks also gate API readiness and Worker process startup before the Tester runs. The printed
table includes FaultId, ReportId, is_mass_issue, Classification, ledger URL, report URL and the check result.
The fifth scenario parses the exact reviewed injection fixture, waits for its report and fails if a
bounded readback of that exact fault ledger contains an action proposal, approval decision, dispatch
start or completion.

Useful variants:

~~~powershell
powershell -ExecutionPolicy Bypass -File scripts/demo.ps1 -NoBuild
powershell -ExecutionPolicy Bypass -File scripts/demo.ps1 -Mock
~~~

`-NoBuild` reuses existing images. `-Mock` adds `compose.mock.yml` and is intended for automated or
deterministic checks, not for validating the product against an actual model.

The injection row is disabled-policy packaging evidence, not provider-delivery evidence. Tester does
not call the approval API, enable an action or contact Telegram/GitHub. Mandatory-Docker integration
coverage separately proves configured-policy and requested-only approval behavior with in-process
recording handlers and zero external provider calls.

Compose host mappings default to API `5198` and PostgreSQL `5432`. Override collisions in the
ignored `.env` file without changing container-to-container URLs:

~~~dotenv
IC_API_PORT=5298
IC_POSTGRES_PORT=55432
~~~

`scripts/demo.ps1` resolves the effective API mapping from Compose, so its health check and the
Tester output follow `IC_API_PORT`.

For deterministic Compose acceptance, run `scripts/demo.ps1 -Mock` once with a fresh PostgreSQL
volume and once with the retained volume. Reset the mock composition only when a fresh run is needed:

~~~powershell
docker compose -f docker-compose.yml -f compose.mock.yml --profile demo down --volumes
~~~

Host-port overrides do not alter the fixed internal addresses `api:8080`, `postgres:5432` or
`otel-collector:4318`. The mock override changes only model and embedding providers. GitHub and
Telegram use fixed production authorities, so their automated doubles are in-process recording
handlers rather than Compose services or configurable endpoint overrides.

## OTLP Collector Demo

The demo profile also runs a pinned stock OpenTelemetry Collector. The Tester emits an error span through
the official .NET OpenTelemetry SDK to the Collector's internal OTLP/HTTP endpoint; the Collector forwards
it with its stock `otlphttp` exporter to IncidentCompass `/v1/traces`. The API maps the standard resource,
span and exception fields into the existing `otel` normalizer, which creates the Signal/Fault/Job path.
No custom Collector processor synthesizes IncidentCompass fields, and Collector-to-API URLs remain internal.

The shipped `Ingestion.Otel` settings default to `ErrorsOnly: true`. Service and severity allow-lists are
empty by default, meaning they do not filter. Metrics, profiles, compressed payloads and protobuf JSON are
not accepted by this release.


Stop demo services with:

~~~powershell
docker compose --profile demo down
~~~

## Local Configuration

~~~powershell
Copy-Item .env.example .env
$env:ConnectionStrings__IncidentCompass = "Host=localhost;Port=5432;Database=incidentcompass;Username=incidentcompass;Password=incidentcompass_dev_password"
~~~

The application reads normal .NET configuration sources. Runtime appsettings.json files include
local OpenAI-compatible defaults, but database credentials still come from environment variables,
user secrets, command-line configuration or dotenv-aware tooling. The `.env` file is ignored by Git
and should stay machine-local.

The triage runtime also loads `config/incidentcompass.config.json`. Route-level model names in that
file are the model names used by the Worker investigation loop:

- `INCIDENTCOMPASS_LLM_MODEL` controls chat routes such as `analysis-chat` and `report-chat`.
- `INCIDENTCOMPASS_EMBEDDINGS_MODEL` controls the `memory-embed` route used by `memory_search`.

`IncidentCompass__ModelGateway__DefaultModel` is still validated as gateway configuration, but it is
not the source of truth for triage route calls. The route config is.

The shipped local-safe profile uses these ceilings:

| Setting | Default |
|---|---|
| Chat `ModelGateway:OpenAiCompatible:TimeoutSeconds`, per HTTP attempt | 300 seconds |
| Embedding `Embeddings:OpenAiCompatible:TimeoutSeconds`, per HTTP attempt | 30 seconds |
| `Orchestrator.Budget.MaxWallClockSeconds`, per investigation attempt | 600 seconds |
| `analysis-chat` and `report-chat` `MaxOutputTokens`, per call | 8000 each |
| `analysis-chat` and `report-chat` `ContextWindowTokens` | 8192 each |
| `Orchestrator.Budget.MaxWorkers` | 6 |
| `Orchestrator.Budget.MaxTokens` | 200000 |
| `Orchestrator.Budget.MaxReprompts` | 2 |
| `Orchestrator.Budget.MaxTurns` | 16 (code default; the shipped config does not set it) |

This is one profile for slower local generation, not a target spend or expected run duration.
`ContextWindowTokens` limits the backend's estimate of prompt size for a route; it does not reserve
or subtract the route's output-token allowance.
The remaining investigation wall clock can cancel a call before its provider timeout. Cloud
operators can tighten `IncidentCompass__ModelGateway__OpenAiCompatible__TimeoutSeconds` through
normal host configuration and lower the route and orchestrator ceilings in their triage config.
Until streaming stall detection is available, allowing longer generation also delays detection of
a real stall.

Chat routes can optionally include `"Reasoning": "off"`, `"low"`, `"medium"` or `"high"`. If
the value is absent, no reasoning-specific provider field is sent and existing routes keep their
current behavior. `MaxOutputTokens` is unchanged: on most servers it remains a limit shared by
reasoning and the final answer.

The checked-in config references `config/incidentcompass.schema.json` for editor completion and
structural feedback. Run the same semantic validator used at startup before launching either host:

~~~powershell
dotnet run --project src/IncidentCompass.Api -- config validate
~~~

The command resolves instruction and output-schema references, expands environment placeholders and
checks routes, roles, immediate versus external tool capabilities, action grants and mode ceilings,
rules, budgets, grouping and redaction settings. It validates without
starting the server or writing a configuration snapshot to PostgreSQL.

If `Redaction.UserIdentifierAttributes` is configured, set the pseudonymization salt only through a
host secret or environment variable, for example
`IncidentCompass__Pseudonymization__Salt`. The salt is intentionally absent from the checked-in
triage config and config snapshots. Without a salt, matching identifiers are replaced with
`[REDACTED]` instead of being persisted in raw form.

The configurable ingestion payload limit must be between 1 KiB and 1 MiB. The upper bound caps
per-request buffering; the 1 KiB lower bound prevents a misconfiguration that rejects ordinary small
OTLP exports. `MaxAttributesBytes` must be positive and no greater than `MaxPayloadBytes`. The
default limits are 64 KiB and 16 KiB.

`IncidentCompass__IngestionLimits__MaxSignalsPerExport` bounds how many records one OTLP export may
carry, independently of its size in bytes: protobuf is compact, so a payload well inside the byte cap
can still hold a very large number of spans or log records, and each record can open a fault and a
triage job. It must be between 1 and 10000 and defaults to 500. The limit is applied to the records
the export carries, before any of them is ingested, so an export above the limit is rejected whole
with `413 Payload Too Large` and stores nothing rather than being ingested in part. This bound is
independent of request rate limiting, which bounds callers per time window rather than work per
request.

## Build And Test

~~~powershell
dotnet restore IncidentCompass.slnx
dotnet build IncidentCompass.slnx
dotnet test --solution IncidentCompass.slnx
~~~

Every project commits a `packages.lock.json` and CI restores with `--locked-mode`. After changing a
package version in `Directory.Packages.props`, regenerate the whole graph with
`dotnet restore IncidentCompass.slnx --force-evaluate` and commit every lock file it changes. See
[Dependency lock files](versioning.md#dependency-lock-files) for why one bump touches several lock
files and how Dependabot pull requests repair themselves.

PostgreSQL repository tests use Testcontainers. Outside CI they skip when Docker is unavailable; in
CI, or when `INCIDENTCOMPASS_REQUIRE_DOCKER_TESTS=true` is set, Docker-backed tests are required and
will fail instead of silently skipping.

## Run Manually

Start local infrastructure:

~~~powershell
docker compose up -d postgres
~~~

The local PostgreSQL image applies the init scripts under `infra/postgres/init` when the Docker
volume is first created. If you are reusing an older local Docker volume, recreate it with
`docker compose down -v` or apply the missing numbered SQL scripts manually.

### Optional Memory Seed

Sample memory lives under `samples/runbooks` and `samples/incidents`. To seed it into local
PostgreSQL, start either host once with memory seeding enabled; the seed path uses the configured
`memory_search` embedding route and therefore the configured embedding provider/model.

~~~powershell
$env:IncidentCompass__Memory__Seed__Enabled = "true"
$env:IncidentCompass__Memory__Seed__TenantId = "local"
$env:IncidentCompass__Memory__Seed__Owner = "default"
$env:IncidentCompass__Memory__Seed__SourceDirectory = "../../samples"
$env:ConnectionStrings__IncidentCompass = "Host=localhost;Port=5432;Database=incidentcompass;Username=incidentcompass;Password=incidentcompass_dev_password"
dotnet run --project src/IncidentCompass.Api --launch-profile http
~~~

To assess documentation freshness, set the reviewed `CurrentReleases` map in the triage config, for example
`"CurrentReleases": { "checkout-api": "0.2.0" }`. The marker is captured in the triage config snapshot;
it is not a deployment webhook or source lookup integration.

The default is startup-only synchronization. To apply file edits and removals without restarting, set
`IncidentCompass__Memory__Seed__RuntimeResyncEnabled=true` and choose a bounded
`IncidentCompass__Memory__Seed__RuntimeResyncIntervalSeconds` value from 1 through 86400. The metadata-only
status is available at `GET /api/v1/health/memory-sync`. The Worker persists this metadata by the configured memory seed tenant and owner, so the API reports the Worker-persisted synchronization snapshot rather than its own local singleton. It is not a Worker liveness probe. When hosts are configured separately, they must use the same memory seed tenant and owner; the standard Compose file supplies the shared values. Seeding is idempotent for the same owner/source/content hash/version.

Run the API:

~~~powershell
$env:ConnectionStrings__IncidentCompass = "Host=localhost;Port=5432;Database=incidentcompass;Username=incidentcompass;Password=incidentcompass_dev_password"
dotnet run --project src/IncidentCompass.Api --launch-profile http
~~~

In a second terminal, run the background worker host:

~~~powershell
$env:ConnectionStrings__IncidentCompass = "Host=localhost;Port=5432;Database=incidentcompass;Username=incidentcompass;Password=incidentcompass_dev_password"
dotnet run --project src/IncidentCompass.Worker
~~~

Useful local endpoints:

- GET http://localhost:5198/api/v1/health
- GET http://localhost:5198/api/v1/users/me
- POST http://localhost:5198/api/v1/incidents
- GET http://localhost:5198/api/v1/faults/{id}
- GET http://localhost:5198/api/v1/faults/{id}/ledger
- GET http://localhost:5198/api/v1/triage-reports/{id}

Sample HTTP requests are available in
[src/IncidentCompass.Api/IncidentCompass.Api.http](../src/IncidentCompass.Api/IncidentCompass.Api.http)
and [samples/http/local-demo.http](../samples/http/local-demo.http).

## Provider Configuration

For manual dotnet runs against a local OpenAI-compatible chat endpoint:

~~~powershell
$env:INCIDENTCOMPASS_LLM_MODEL = "<chat-model-id>"
$env:IncidentCompass__ModelGateway__Provider = "OpenAiCompatible"
$env:IncidentCompass__ModelGateway__OpenAiCompatible__BaseUrl = "http://localhost:1234"
$env:IncidentCompass__ModelGateway__OpenAiCompatible__ChatCompletionsPath = "/v1/chat/completions"
$env:IncidentCompass__ModelGateway__OpenAiCompatible__ApiKey = "local-dev-key"
$env:IncidentCompass__ModelGateway__OpenAiCompatible__AllowInsecureHttpForLoopback = "true"
~~~

To enable reasoning for a logical provider, configure its explicit request protocol under
`IncidentCompass:ModelGateway:OpenAiCompatible:ReasoningModes`. The map key must match the route's
`ProviderId`; its value is `Disabled`, `ReasoningEffort` or `ChatTemplateKwargs`. This selection is
not inferred from the model name. `ReasoningEffort` sends lowercase `reasoning_effort` values, with
`off` mapped to `none`. `ChatTemplateKwargs` sends
`chat_template_kwargs.enable_thinking`, mapping `off` to `false` and every enabled level to `true`;
it does not preserve intensity, and a local server may ignore it. A missing mapping sends no
reasoning-specific field.

For embeddings:

~~~powershell
$env:INCIDENTCOMPASS_EMBEDDINGS_MODEL = "<embedding-model-id>"
$env:IncidentCompass__Embeddings__Provider = "OpenAiCompatible"
$env:IncidentCompass__Embeddings__OpenAiCompatible__BaseUrl = "http://localhost:1234"
$env:IncidentCompass__Embeddings__OpenAiCompatible__EmbeddingsPath = "/v1/embeddings"
$env:IncidentCompass__Embeddings__OpenAiCompatible__ApiKey = "local-dev-key"
$env:IncidentCompass__Embeddings__OpenAiCompatible__AllowInsecureHttpForLoopback = "true"
~~~

OpenAI-compatible provider URLs must use HTTPS by default. Insecure HTTP is allowed only for explicit
loopback/local development configuration. Docker Compose enables that local allowance so containers can
reach a host-side model server through `host.docker.internal`.

## Demo Identity

Demo identity can be supplied with headers:

~~~http
X-Demo-User-Id: alice
X-Demo-Tenant-Id: local
X-Demo-Roles: developer,admin
X-Demo-Groups: demo,engineering
~~~

Demo header auth is enabled by default only when the API runs in Development, where missing headers
fall back to local demo-user defaults. Do not use X-Demo-* headers as deployed authentication.
