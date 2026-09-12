# IncidentCompass 0.1.1 - MVP hardening patch

> Historical record, partly superseded (2026-09-11): the Development worker lease and the shipped
> investigation wall-clock budget described below are now 720 and 600 seconds, raised in 0.4.0 for
> local generation. The invariant that the lease exceeds the budget is unchanged and still tested. See
> [0.4.0](release-notes-v0.4.0.md) for current behaviour.

IncidentCompass 0.1.1 strengthens the first public reference release without adding a new product
surface. The patch focuses on correctness, privacy, concurrency safety and reproducible local use.

## What changed

- **Configurable Compose host ports.** IC_API_PORT and IC_POSTGRES_PORT avoid collisions with
  services already running on the host. Container-to-container ports and the original defaults stay
  unchanged, and the demo script discovers the effective API mapping.
- **Attempt-isolated retry context.** A retried investigation now receives job-level intake facts
  plus artifacts from its current attempt. Worker outputs and tool results from failed prior
  attempts no longer enter the next model context as if they belonged to the active run.
- **Typed-field secret redaction.** Redaction now covers normalized string fields such as external
  ids, trace/span ids, service and operation names, error details, descriptions and HTTP route
  fields before fingerprinting, persistence and model use. JSON attributes and bodies remain
  covered by the existing recursive redactor.
- **Serialized grouping and fault completion.** Signal attachment locks and rechecks the candidate
  fault inside the intake transaction. If report publication closed the fault concurrently, the
  transaction rolls back and grouping is resolved again against committed state.
- **Safer Development worker lease.** The Development override is now 300 seconds, above the
  shipped 120-second investigation wall-clock budget. A regression test protects this cross-file
  invariant.
- **PostgreSQL error normalization.** Active persistence adapters translate Npgsql and database
  timeout failures into an Application-level PersistenceException with a safe public message.
  Cancellation and application/domain failures keep their original semantics.

## Compatibility

- No database schema migration is required.
- No HTTP endpoint or response contract changed.
- Compose keeps 5198 for the API and 5432 for PostgreSQL unless explicitly overridden.
- The four feature commits reserved for 0.2.0 are not part of this patch.

## Verification

The release branch passed:

- locked-mode dependency restore;
- Release build with zero warnings;
- format verification;
- code-organization and package-vulnerability gates;
- 95 unit tests;
- 128 Docker-backed integration tests.

The optional real-local-LLM smoke test remains opt-in and was not part of the deterministic release
gate.
