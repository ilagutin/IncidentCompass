# IncidentCompass

[![CI](https://github.com/ilagutin/IncidentCompass/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/ilagutin/IncidentCompass/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/ilagutin/IncidentCompass)](https://github.com/ilagutin/IncidentCompass/releases/latest)
[![License](https://img.shields.io/github/license/ilagutin/IncidentCompass)](LICENSE)

A .NET 10 backend that turns an incident signal into a reviewable, evidence-backed triage report.

The model chooses investigation steps and proposes tool calls. The backend owns credentials, tool
grants, budgets, evidence checks, approvals and external actions.

Version **0.3.0** supports read-only source and GitHub context, a host-managed API-key tenant
boundary, governed Telegram/GitHub actions and model-cost rollups.
See the [release notes](docs/release-notes-v0.3.0.md) for changes and verification.

## One investigation

1. Accept an OTLP trace/log export or an OTel-shaped, user or tester signal; redact it and create a durable triage job.
2. Investigate under the job's versioned configuration, using role-scoped memory, source and ticket tools.
3. Publish a report only after the backend resolves its evidence against allowed stored artifacts.
4. When configured, propose a Telegram notification or GitHub issue action. The backend selects the
   target and freezes the payload; GitHub writes require operator approval.
5. Inspect the report, policy decisions, model usage and action outcomes through the API and durable ledger.

![IncidentCompass deterministic demo report](docs/images/incidentcompass-demo-report.png)

This example is from the deterministic **0.1.1** demo: a `KnownIncident` report with one cited runbook
and 22 ledger events. It illustrates report review, not current-version model accuracy.

## Try the local flow

Prerequisites: Docker Compose, the .NET 10 SDK for configuration validation, PowerShell and
OpenAI-compatible chat and embedding endpoints. On Windows and macOS, Compose uses
`host.docker.internal:1234` by default.

~~~powershell
Copy-Item .env.example .env
# Set exact chat and embedding model ids in .env.
dotnet run --project src/IncidentCompass.Api -- config validate
powershell -ExecutionPolicy Bypass -File scripts/demo.ps1
~~~

The script builds PostgreSQL, API, Worker and Tester containers, runs the local scenarios and prints
report and ledger URLs. Use `scripts/demo.ps1 -Mock` for the deterministic provider path.

The additive production path is intentionally separate from that demo. See the
[single-host production runbook](docs/single-host-production.md) for loopback-only Compose startup,
preflight, bounded PostgreSQL backup and fresh-volume recovery on one trusted machine.

Compose host-port overrides do not change the fixed internal API, PostgreSQL or OTLP addresses.
The mock overlay replaces only model and embedding providers. GitHub and Telegram adapters retain
fixed production authorities and are tested with in-process recording handlers, not Compose endpoint
doubles or configurable provider URLs.

See [Quickstart](docs/quickstart.md) for setup and [Local demo walkthrough](docs/local-demo.md)
for scenarios, ports and expected output. Optional source, GitHub, Telegram and API-key settings
are in [Integration configuration](docs/integrations.md).

## Guarantees and limits

- Unknown, ungranted or denied tools fail closed through backend policy.
- Reports cite stored evidence from the allowed job and attempt. Valid citations do not prove a correct diagnosis.
- Backend budgets bound investigation work. A single in-flight model call can overshoot the token limit;
  the excess is recorded and further calls stop.
- Approved actions use frozen payloads and at-most-once backend invocation. An uncertain external
  outcome is recorded for operator review and is never automatically resent.
- Automated tests use deterministic providers by default. The mock injection scenario checks the
  disabled-action configuration; separate integration tests exercise configured policy and approval.
- Full rendered prompt/body logging stays disabled by default.

This is reference-quality software for local review. Demo authentication and Compose defaults are
local-only. Source lookup and external integrations require explicit host configuration; external
actions ship disabled. The project does not provide enterprise identity/RBAC, a UI, general incident
correlation, automated code fixes or a production incident platform.

## Explore the implementation

IncidentCompass is a layered monolith: API and Worker compose Application use cases and Infrastructure
adapters around a provider-independent Domain. PostgreSQL stores jobs, configuration snapshots,
incident memory, reports, approvals and the audit ledger.

- [Signal intake](src/IncidentCompass.Application/Intake/IngestSignal/Handler.cs)
- [Governed investigation](src/IncidentCompass.Application/Investigation/Jobs/GovernedTriageInvestigationProcessor.cs)
- [Shared tool policy](src/IncidentCompass.Application/Governance/Tools/ToolRuleEngine.cs)
- [Report evidence grounding](src/IncidentCompass.Infrastructure/Investigation/PostgresReportEvidenceGrounder.cs)

Read [Architecture](docs/architecture.md), [Security model](docs/security-model.md) and
[Trade-offs](docs/trade-offs.md) for the boundaries and their costs.

## Documentation

- [Quickstart](docs/quickstart.md), [Local demo](docs/local-demo.md) and [Integration configuration](docs/integrations.md)
- [Architecture](docs/architecture.md) and [Application pipeline](docs/application-pipeline.md)
- [Security model](docs/security-model.md) and [Security policy / vulnerability reporting](SECURITY.md)
- [Model gateway](docs/model-gateway.md)
- [Observability](docs/observability.md) and [Cost tracking](docs/cost-tracking.md)
- [Code organization](docs/code-organization.md) and [Trade-offs](docs/trade-offs.md)
- [Versioning and release flow](docs/versioning.md)
- [Single-host production runbook](docs/single-host-production.md)
- [Changelog](CHANGELOG.md)
- Release notes: [0.3.0](docs/release-notes-v0.3.0.md), [0.2.0](docs/release-notes-v0.2.0.md),
  [0.1.1](docs/release-notes-v0.1.1.md) and [0.1.0](docs/release-notes-v0.1.0.md)

## Origin

IncidentCompass was bootstrapped from
[dotnet-genai-starter](https://github.com/ilagutin/dotnet-genai-starter)'s layered .NET structure and
model/embedding gateway boundaries, then specialized into incident triage. General chat, document
ingestion and MCP product surfaces are outside this repository's scope.
