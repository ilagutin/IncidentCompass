# IncidentCompass 0.4.1 - the release's own claims, made true

0.4.1 is a patch. It adds no capability, changes no default and grants nothing new. Every fix in it
undercut something [0.4.0](release-notes-v0.4.0.md) already claimed in public, and the point of the
release is that those claims now hold without a footnote.

Four defects, none of which was a live leak or a live outage with the shipped configuration. Each was
a way a stated guarantee could quietly stop being true.

## What changed

### The host survives a database that is not ready yet

The shipped Compose files already gate the API and the Worker on `depends_on: postgres` with
`condition: service_healthy`, and 0.4.0 corrected that health check to answer over TCP so the gate is
real. What neither 0.4.0 nor anything before it had was the application's own account of the wait:
a host started outside that gate, or one that reached the database in the window after the health
check passed, treated a refused first connection as fatal, and what recovered it was the container
restart policy. Nothing in the application expressed the wait, so nothing could be configured,
observed or reasoned about.

The wait is now a bounded budget at `IncidentCompass:Postgres:StartupRetry`, with `MaxAttempts`,
`InitialDelayMilliseconds`, `MaxDelayMilliseconds` and `MaxTotalDurationSeconds`, validated at both
ends when the host starts. Four failure shapes are retried and nothing else: a socket failure other
than a hostname that does not resolve, any `IOException`, which is how a connection accepted and then
dropped mid-handshake arrives, a timeout, and SQLSTATE `57P03`. A wrong credential, an unknown
database and an unparsable connection string still fail on the first attempt, because waiting cannot
change any of them.

The budget covers the first successful connection only. Once one connection has opened, every later
failure surfaces at once, so a database that disappears in steady state is not hidden behind silent
reconnects. Exhausting the budget fails the host loudly with the normalized persistence error. The
Compose restart policies stay where they are, `unless-stopped` in the production overlay and
`on-failure` on the API and the Worker in the base file, as a second line rather than as the thing
that implements startup ordering. The expiry is checked between attempts, so worst-case wall time is the
expiry plus one connection timeout.

### A model client cannot escape the bounded caller unaccounted

The governed model caller raises one exception for a failed call, and that exception carries two
facts every caller needs: which provider failure kind ended the call, and what durable `ModelCall`
accounting is still owed for a call the provider may already have billed. It carried them only when
the client raised a normalized provider exception. Anything else propagated unwrapped, which made
that exception non-exhaustive and defeated the reason it exists.

The port now states the contract and the caller enforces it rather than trusting adapter discipline.
A client that raises anything but a normalized provider exception or a caller-driven cancellation is
in breach, and the breach becomes the same recorded, classified failure a normalized error produces,
under the error code `provider_contract_violation`, with the offending exception kept beneath the
synthesized one so it stays readable in a stack trace and reaches no persisted row.

Containment is not absolution, and the record says so. The row names no answering adapter and carries
no usage, so the call counts and is never priced, and a provider that billed for it stays invisible
to the cost roll-up. The failure kind is unclassified unless the offending chain carries a classified
provider failure of its own, in which case that kind decides retry and fail-over while the error code
still names the breach. A contract violation is a defect to fix in the adapter, not a supported
failure mode.

### The redaction boundary covers the whole artifact

`docs/security-model.md` describes one place where tool-produced text becomes durable triage state.
Three surfaces reached durable state without passing it.

A tool can no longer express a domain reference as free text. It is built through a bounded type that
renders `kind:segment[:segment...]` and refuses control, format, unassigned, private-use and
ill-formed code points, whitespace other than the plain space, and the separator, capped at 200
UTF-16 code units per segment and 512 overall. Refusing the ill-formed case means refusing the
replacement character U+FFFD as well, so a real filename containing one costs that match; a space and
an emoji are accepted, because a real checkout holds those too. The rendered reference then meets the
same redactor the payload meets, and the row's redaction marker is true when either changed.

Where a reference is built from connector text of unbounded shape, one that cannot be built is
refused rather than thrown: a source match that cannot be named is dropped with the new
`source_reference_rejected` limitation, and a delegated worker keeps its answer and degrades only the
reference. A release id or role key that could never be expressed is rejected when the configuration
loads rather than at the first lookup. The two remaining call sites still use the throwing form,
because their segments are a literal, a validated repository name, a parsed integer and a GUID, where
a refusal would be a defect in this repository rather than a value a connector chose.

The `ToolResult` row's redaction marker now answers for the whole tool call. The two citable kinds a
tool call produces have to agree, because a model that could find one marked and the other silent
would choose whether a published report admits a withholding by choosing which of them to cite.

The other two surfaces are stated rather than changed, and the document now says where the boundary
ends. The action-result writer records a dispatched action's own result by direct SQL inside the
dispatch transaction, with no triage configuration in scope, so for that one kind the column name
describes the boundary and not those bytes. Rows written before the boundary existed are not
rewritten: the content hash describes the payload bytes that were stored, and editing durable
evidence so that a later guarantee reads as though it always held is the shape this project refuses
elsewhere. Both statements name which rows are affected, how far they reach and what an operator can
conclude.

### A spend figure says what it covers

0.4.0 made the cost roll-up produce a real figure for the first time. It could not contain an
embedding call, and nothing said so. An embedding call writes no `ModelCall` row at all, so it is
absent from every number the roll-up produces rather than merely unpriced: not in the call counts,
not in the token totals, not in any currency's spend. The documentation said only that the system
holds no embedding charges, which reads as a missing price.

`docs/cost-tracking.md` now states the mechanism and its consequence, and the response carries a
`spendCoverageStatement` so a caller reading the JSON learns it without finding the file first. Both
are worded by call kind and by adapter, never by the provider, so they stay true when an embedding is
served by an in-process adapter that costs nothing, and neither claims an invoice exists to go and
read. The pricing table's `embedding_token_price_per_million` column, which nothing reads, is named
where an operator would otherwise assume it is in use.

No accounting changed. No ledger row, no pricing, no schema. Accounting for external embedding calls
remains unbuilt and is not promised here.

## Defects found while fixing the defects

Four things went wrong in the fixes themselves and were caught before they were committed. Recording
them is more useful than claiming a clean pass, and each one is checkable in the code that shipped.

- The bounded domain-reference type threw where a plain string never did, and nothing caught it: the
  exception escaped the tool, the executor, the role runner and the delegate path, none of which
  classify it, so a repository-relative path over 200 characters would have dead-lettered a triage
  job with no misconfiguration involved. The non-throwing form and the degradations built on it are
  what cover that path; the configuration-load check covers the two cases a misconfigured release id
  or role key would have caused.
- The first attempt at that type filtered on control characters and whitespace, which are Unicode
  category Cc and the whitespace categories. Zero-width space, the bidirectional overrides, the byte
  order mark and the Unicode tag block are category Cf and passed, and because the check iterated
  UTF-16 code units rather than runes, every astral code point passed unconditionally.
- Widening the per-item redaction marker to cover the domain reference broke the pairing the report's
  withholding sentence rests on, until the `ToolResult` marker was widened with it.
- Narrowing the startup retry predicate the first time removed a real case: a connection accepted and
  dropped mid-handshake, which Npgsql reports with no socket error and no SQLSTATE.

## Compatibility

Upgrading from 0.4.0 needs no action for a deployment whose configuration is already valid.

- `GET /api/v1/observability/cost-rollups` gains one response property. Nothing was removed, renamed
  or reordered.
- Two configuration values are validated that were not before: every `CurrentReleases` entry and every
  `Roles` key must be expressible as a domain-reference segment, which means no colon, no control or
  format character, no whitespace other than the plain space, and at most 200 UTF-16 code units. No
  shipped configuration or fixture is affected. A deployment that used, for example, an ISO-8601
  timestamp with colons as a release id must rename it before upgrading: the host refuses to start,
  and because stored configuration snapshots are re-validated whenever a job is rehydrated, such a
  deployment also could not resume its existing jobs.
- No database migration, no schema change and no pricing change. A 0.4.0 volume upgrades in place.
- Defaults are unchanged. The startup-retry budget's defaults are 10 attempts, 250 ms growing to
  2000 ms, and a 30 second expiry.
- Two source-level changes affect anyone compiling against these assemblies rather than consuming the
  API: `ToolArtifactDraft` takes and exposes an `ArtifactDomainRef` where it took a `string`, and an
  `IAiModelClient` implementation that raises a non-normalized exception now produces a recorded
  contract violation instead of propagating. Neither is packaged and this project is still `0.x`, but
  a fork carrying its own tool or model adapter will notice both.
- `source_lookup` can now return the limitation `source_reference_rejected`, and when every hit is
  dropped for that reason its output reports `matched: false` while `outcome` stays `matched`: the
  read boundary did find files, and the limitation is what says why none survived to be cited.
- Two log event ids are new, 2601 and 2602, both from the startup connection wait. They are in the
  Infrastructure range and documented in `docs/observability.md`.

## Verification

The release gate runs locked-mode restore, build with zero warnings and zero errors, formatting
verification, the code-organization gate, the package-vulnerability gate and the internal-reference
gate.

`dotnet test --solution IncidentCompass.slnx` runs the full suite. On the maintainer's machine with
Docker available and `INCIDENTCOMPASS_REQUIRE_DOCKER_TESTS` set, that is 2,002 tests: 1,998 passed,
0 failed, 4 skipped. The four skips are the same structural ones 0.4.0 recorded: three
symlink-dependent assertions that need a privilege the running process may not have, and the explicit
OpenAPI-baseline regeneration fact that only `scripts/update-openapi-baseline.ps1` targets. Only the
last of those is structural everywhere; the symlink three run on the CI runner, so a CI run reports
one skip rather than four.

The startup-retry tests are timing-sensitive and CI runs on Linux, so they were also executed inside
a Linux container against a sibling PostgreSQL container rather than only on the maintainer's
machine. They passed there with nothing skipped.

What that run does not cover is unchanged from 0.4.0 and worth repeating: no automated test reaches a
real model or embedding provider, the Docker-backed integration coverage is enforced by an opt-in
environment variable rather than by default, and no real Telegram or GitHub endpoint is called
anywhere in the suite.
