# IncidentCompass 0.2.0 - governed triage hardening

IncidentCompass 0.2.0 is a reference-quality local release. It adds governed reliability, memory,
report-history and release-process hardening without claiming production readiness or enterprise
authentication.

## What changed

- Native OTLP/HTTP trace and log intake is exercised by the shipped .NET OpenTelemetry SDK Tester
  through a stock Collector route into the normal IncidentCompass intake pipeline.
- Delivery deduplication, versioned grouping, suppression, recurrence escalation and exactly-once
  recurrence re-triage preserve durable incident facts under concurrent intake.
- File-backed incident memory now reconciles safely across hosts, rejects divergent or partial scans,
  supports bounded runtime resync and exposes Worker-persisted metadata-only sync health through the API.
- Published reports are immutable, can supersede earlier reports after recurrence re-triage, and have
  tenant-scoped compact history/list APIs with bounded stable keyset pagination.
- Incident-data tenancy is server-owned: v0.2.0 uses `Ingestion.DefaultTenant`, ignores caller,
  envelope and OTLP tenant hints, and consistently returns `404` for out-of-scope reads. This is a
  local data partition, not authentication.
- Worker leases renew while an investigation runs; provider outages create a durable delayed state,
  apply bounded per-process claim backpressure and recover after a successful provider call.
- Metadata-only `ActivitySource`/`Meter` signals cover job claims and attempts, model and governed
  tool calls, migrations and memory sync. Tags have a closed outcome vocabulary and omit prompts,
  credentials, identifiers and incident content.
- Release publication now pins and verifies the event SHA, runs the Docker-backed full test gate
  before tagging, uses one publisher, and supports idempotent main-only recovery dispatch.

## Compatibility and limits

- Database migrations support both fresh v0.2.0 databases and populated v0.1.1 upgrades.
- The API remains under `/api/v1`; report list responses add only the documented v0.2.0 surface.
- Demo headers and the local incident tenant are not a security boundary. API-key authentication and
  server-side tenant mapping remain outside this release.
- Full prompt/body logging remains disabled. Runtime telemetry is a source only; this release does
  not configure an OTLP runtime exporter or a metrics endpoint.
- This is not a production incident platform. It does not provide enterprise auth, distributed
  provider breaker state, retention/re-embedding operations, a UI or external actions.

## Verification

The release candidate passed cold and warm deterministic Compose demos on a fresh then retained
PostgreSQL volume with `IC_API_PORT=5298` and `IC_POSTGRES_PORT=55432`. Both runs exercised the real
OpenTelemetry SDK to Collector to IncidentCompass intake route and completed all four shipped Tester
scenarios. The final release gate records restore, build, default and Docker-backed tests, formatting,
organization and vulnerability checks on the release branch before publication.
