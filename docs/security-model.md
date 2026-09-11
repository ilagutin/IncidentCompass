# Security Model

The core principle is simple: the LLM is not a security boundary.

The backend decides what data and tools are available. The model may summarize, reason and propose actions, but it must not enforce authorization or receive privileged credentials.

## Demo Auth

IncidentCompass uses:

- `IUserContext` in the application layer;
- demo/fake authentication for local development;
- headers, seeded users or configuration as demo identity sources.

Real auth providers such as Entra ID or ASP.NET Identity are future adapters, not requirements for the local sample path.

The API registers the demo header-based `IUserContext` only for `Development` by default (`appsettings.Development.json` sets `IncidentCompass:DemoAuth:Enabled` to `true`; the base `appsettings.json` leaves it `false`). Development requests may omit headers and use the configured local `demo-user` defaults for the quickstart. Startup fails unless the API composition root registers a real foreground `IUserContext` adapter before the app starts: `ApiUserContextSetup.AddApiUserContext` always registers `ApiUserContextStartupFilter` (`src/IncidentCompass.Api/Security/ApiUserContextStartupFilter.cs`), and that filter throws unless a non-background `IUserContext` was resolved. In practice this only bites in Production and other environments where neither API-key auth nor demo auth ends up enabled, because Development and explicit demo opt-in register `DemoHeaderUserContext` first. The Infrastructure project registers `IBackgroundUserContext` for Worker/system jobs, not a foreground API `IUserContext`, so the background identity cannot satisfy the API auth requirement by DI ordering. Non-production demo environments can explicitly opt in to demo headers; in that opt-in mode, the configured default user, tenant, roles and groups are ignored, the request must include an explicit `X-Demo-User-Id` to be treated as authenticated, and anonymous requests receive no default claims. Worker hosts explicitly map the background context for job processing and do not use HTTP demo headers.

Demo headers such as `X-Demo-User-Id`, `X-Demo-Tenant-Id` and `X-Demo-Roles` are caller-controlled sample inputs. They are useful for local walkthroughs, but they are not authentication and must not be trusted in deployed environments.

## API-key boundary

The API host supports a minimal shared-key boundary through host-only
`IncidentCompass:ApiKeyAuth` settings. It ships disabled: `src/IncidentCompass.Api/appsettings.json`
sets `IncidentCompass:ApiKeyAuth:Enabled` to `false`, with an empty `Credentials` list, and no
environment-specific appsettings file turns it on. A deployment that wants the shared-key boundary
must explicitly set `Enabled` to `true` and supply credentials through host configuration. When `Enabled` is true, a fallback authorization policy
protects all current and future endpoints unless they are explicitly anonymous. The complete
anonymous allowlist is `/health`, `/api/v1/health`, `/api/v1/health/memory-sync`,
`/api/v1/health/memory-corpus` and the Development-only OpenAPI document. The memory-corpus route
carries the same class of content as the memory-sync route: configured route and provider
identifiers, a model name, a vector width, counts and a generation id. It reaches no document text,
no chunk text, no vector, no provider endpoint and no credential. Manual intake, incident-data reads, `users/me` and native OTLP
trace/log ingestion all use the same boundary.

Action approval routes use a dedicated operator policy. The complete
`/api/v1/action-approvals` group returns `403` before user-context resolution, dispatch or repository
access whenever API-key authentication is disabled. When it is enabled, any valid host-issued key is
the minimal action operator for exactly its mapped tenant until RBAC is added. Demo identity, demo
headers and the config-default tenant never grant approval authority. Foreign action ids return `404`.

Clients send exactly one `X-IncidentCompass-Key` value. It must be 32-128 ASCII base64url
characters with no padding, commas or whitespace. The host stores only its SHA-256 hex digest and
compares the digest in fixed time. A credential also has a non-secret stable key id and exactly one
tenant id. Successful authentication supplies both `IUserContext` and `IIncidentTenantContext`
from that server-owned mapping, so request bodies, OTLP attributes and demo headers cannot choose
the tenant. Missing, malformed and invalid credentials return `401` before endpoint binding or
Application dispatch.

`Enabled`, `PermitLimit` and `WindowSeconds` are startup-static. Protected requests share a
queue-free fixed-window limiter partitioned only by authenticated key id; anonymous health and
OpenAPI requests are not limited. A valid configuration reload atomically rotates the immutable
credential map. An invalid reload, or an attempted live change to a startup-static field, installs
a deny-all map until a fully valid configuration with the original static fields arrives or the
process restarts. This avoids retaining a potentially revoked credential during a broken reload.

Rejects increment `incidentcompass.api.authentication.rejections` with only the bounded outcome
`missing`, `malformed` or `invalid`. Raw keys, configured digests and request bodies are excluded
from auth logs, metrics, errors, the triage ledger and configuration snapshots. Host transport may
return `431` before application code for a header block above its own size limit.

Credentials are injected through host configuration or environment variables. They are not public
triage configuration and no raw key belongs in tracked files. For example, credential fields use
`IncidentCompass__ApiKeyAuth__Credentials__0__KeyId`, `__TenantId` and `__Sha256Digest` suffixes.
This is minimal authentication, not RBAC, key distribution, a secret store, OAuth or a production
identity platform.

## Model Provider Credentials

A provider entry in the triage configuration names its credential rather than carrying it. Its
`ApiKeySecretRef` holds the **name** of an environment variable, and the value is read from the
process environment at the moment a call needs it.

That indirection exists because the triage configuration is not a private file. It is tracked, it is
reviewed, it is canonicalized and hashed into a `config_hash`, and the hashed document is persisted
as a triage-configuration snapshot in PostgreSQL. Anything written into it is written into all of
those. The `${VAR}` and `${VAR:-fallback}` placeholders elsewhere in that file are expanded *into*
the loaded document, which is exactly why a credential must never be written as one: the expanded
value would be hashed and stored. `ApiKeySecretRef` keeps the variable's name in the snapshot and
resolves the value outside everything that is stored.

There is no secret store here, and this is not one. The only resolver reads environment variables,
which is the same channel the API-key credentials above arrive through.

A provider credential is never logged, never included in an error message and never returned in a
response. The specific guarantees:

- The adapters that hold a resolved credential are inside the directories
  `ModelGatewayLoggingGuardTests` scans, which contain no output sink of any kind.
- The two types that carry a resolved credential are classes rather than records, so neither prints
  its credential from an interpolation or a structured-logging argument anywhere in the process.
  A record's generated `ToString` would have printed it.
- A configuration failure names the setting and the environment-variable name, which are the only
  actionable parts, and never a value - including the value of a *different* provider's credential
  that did resolve.
- A provider HTTP failure is normalized to a status code and an error code; the request that failed
  is not echoed.

Host configuration remains an alternative source: `IncidentCompass__ModelGateway__OpenAiCompatible__ApiKey`
and its embedding counterpart are the default provider profile's credential, and a single-provider
configuration keeps using them. See `docs/model-gateway.md`, "Providers", for exactly when that
default applies and when a configuration must supply per-provider credentials instead.

## Incident Data Tenant Scope

`IIncidentTenantContext` is separate from `IUserContext`. With API-key authentication enabled it
reads the tenant mapped to the authenticated key. In explicitly auth-disabled local/demo mode it
reads `Ingestion.DefaultTenant` from the server-loaded triage configuration. `X-Demo-Tenant-Id`,
incident-envelope fields, OTLP resource attributes and other sender-controlled data never select
the incident-data tenant.

Fault, ledger, report and action approval read/decision paths resolve this server-owned scope before querying. An object
outside the scope is indistinguishable from a missing object and returns `404`; compact report
lists only return scoped rows. API-key authentication changes only the API composition adapter,
not the intake or read use cases. Worker/system jobs continue to use their background identity and
the server-loaded job/configuration tenant context.

## Local Compose Credentials

`docker-compose.yml` sets local-only PostgreSQL demo defaults through `${VAR:-default}` fallbacks:
`POSTGRES_USER` and `POSTGRES_PASSWORD` default to `incidentcompass` and
`incidentcompass_dev_password`; the `postgres` healthcheck and the `api` and `worker`
`ConnectionStrings__IncidentCompass` values reuse the same fallbacks. These values exist only so the
one-command demo runs without an operator supplying anything; overriding them from an ignored `.env`
file or shell variables replaces them without editing the compose file (see `docs/local-demo.md` and
`docs/quickstart.md`).

The repository ships four compose files, and only the first carries demo credentials:

- `docker-compose.yml` is the local demo stack. It is the only file that supplies default database
  credentials, and it must not be deployed as-is.
- `compose.mock.yml` is a deterministic-provider overlay for that demo stack. It replaces the model
  and embedding providers and nothing else.
- `compose.evaluation.yml` is a local evaluation overlay for that demo stack. It closes the published
  ports, mounts the evaluation triage configuration read-only and clears the Telegram and GitHub
  credentials for the run.
- `compose.production.yml` is the bounded single-host overlay described in
  `docs/single-host-production.md`. It carries no demo credentials of its own: the database,
  provider, API-key and source values it sets are required variables, so Compose refuses to start
  when one is missing and the base file's demo defaults cannot take effect. It re-declares the three
  database credential variables on its own `postgres` service rather than inheriting them, so the
  database server it starts cannot fall back to the demo name, user or password no matter what the
  rest of the file requires. Demo auth is forced off, and the API and PostgreSQL ports bind to
  loopback. It is a reference deployment for one trusted machine and one trusted operator, not a
  hardened multi-tenant production template.

A real deployment must still supply its own configuration and secrets management. Neither
`docker-compose.yml` nor its default credentials may be deployed as-is.

## Logging

Full rendered prompt logging is disabled, and there is no switch behind that: no setting in this
repository enables it, and none is planned. A reader looking for the option to turn it on will not
find one, because the position is not implemented by an option.

Part of it is implemented by absence, and that part is enforced. Four directories are kept free of
every output sink - the port contracts in `src/IncidentCompass.Application/Core/ModelClients` and
`src/IncidentCompass.Application/Core/Embeddings`, and their adapters, provider DTOs and mock
clients in `src/IncidentCompass.Infrastructure/ModelGateway` and
`src/IncidentCompass.Infrastructure/Embeddings`. This is where a fully rendered request is handed to
a provider and where a raw response body is read back, so it is the place where a stray debug line
would be most likely to carry the whole of either. `OpenAiCompatibleModelClient` takes no `ILogger`;
neither does any file beside it, and none of them writes to the console, to a trace listener or to a
file either.

`tests/IncidentCompass.UnitTests/ModelGatewayLoggingGuardTests.cs` asserts that absence over those
four directories. Injecting a logger into any of them, or leaving a `Console.WriteLine` behind while
debugging a provider response, fails the test suite instead of passing quietly. The test is
deliberately about sinks rather than about what a particular log call would have written: a
placeholder name in a message template cannot tell a reviewer, or a matcher, whether the argument
behind it carries provider text. The cost of that strictness is that a genuinely safe number cannot
be logged from inside those files either, which is the intended answer rather than an oversight -
model-call metadata is recorded from the accounting path outside this boundary, where the values
being written are backend-derived.

Be precise about the scope of that. Those four directories are not the whole of the code that
handles prompt text or provider bodies, and the guard does not claim they are. The prompt is
assembled in `src/IncidentCompass.Application/Investigation/Jobs` -
`TriageInvestigationPromptBuilder` builds the payload lines and `InvestigationModelCaller`
constructs the request from them - and that folder does log, from `InvestigationModelCaller`. A raw
provider error body is parsed in `src/IncidentCompass.Infrastructure/OpenAiCompatible`, by
`OpenAiCompatibleErrorMapper.TryReadError`, which the guard does not scan. In all of that
surrounding code the rule that only metadata is logged holds today by reading it: the log calls
there are `LoggerMessage`-generated templates over job ids, attempt numbers, role and route names,
tool names, bounded reason codes, exception type names and durations, never over a message list, a
payload or a response body. That is
convention plus code review, not a structural impossibility, and it is not something a text scan can
be widened to prove - a file that legitimately logs metadata and a file that logs a prompt look the
same to a matcher. Treat a change in that folder as a change that needs the reviewer to check what
is being logged, not as one the guard will catch.

The rest of the logging rules:

- Metadata logging is allowed: request ID, user ID, model, tokens, cost, status. It is written
  outside the four guarded directories above, by `InvestigationModelCaller` and the triage ledger,
  from backend-derived values rather than from provider text (see `docs/observability.md`).
- Tool execution is controlled by backend policy. The model may propose tool calls, but it cannot execute tools directly and never receives infrastructure credentials.
- The API error boundary never echoes an exception's own message to a client. `NotFoundException`,
  `ConflictException`, `ForbiddenRequestException` and `ValidationException` map to a `ProblemDetails`
  response carrying a stable `errorCode` and an authored, client-safe `detail`; the original exception
  is logged server-side only, tagged with the request's correlation id (`HttpContext.TraceIdentifier`)
  and the same error code (see `docs/observability.md`).

## Cost read boundary

The authenticated model-cost endpoint obtains its tenant only from the host-bound `IUserContext` and
accepts no tenant selector. Its bounded window query joins ledger rows through tenant-owned faults.
The response exposes UTC buckets, counts, token totals and per-currency spend only, not tenant, fault,
job, provider, model or logical route identifiers. Malformed ModelCall history and missing or
overlapping prices fail closed to unpriced and are never echoed to responses or logs. Pricing remains
operator-maintained database configuration; this read surface grants no price, alert or quota authority.

## Fault status read boundary

`GET /api/v1/faults/{id}` reports why a triage job is waiting through two projected job columns:
`lastErrorCode` and `nextAttemptAtUtc`. The code comes from a closed, application-owned vocabulary
that the Worker decides before any durable write, so a provider name, a provider message, an HTTP
status line, an endpoint or an upstream error code cannot reach it: an adapter keeps the raw upstream
code in a separate `ProviderException.ProviderErrorCode` property that the failure classifier never
reads. The sibling `last_error_message` column is not projected at all, because its ordinary form
carries the raising exception's type name. See `docs/observability.md`, "Why a waiting job is
waiting".

## Local source read boundary

The source worker never receives a filesystem root, release selector or arbitrary read argument.
Host options allowlist exact service/release roots and optional build-path prefixes; the job's
snapshotted `CurrentReleases` entry is the only release selector. Candidate paths are canonicalized
and revalidated below the selected root before opening, reparse/symlink traversal is rejected, and
only configured text extensions within byte, frame, candidate and excerpt limits are read. Source
bodies and absolute host paths are not logged or persisted. Durable artifacts contain only a
repository-relative path, bounded excerpt, line range, release and `heuristic` mapping label, and the
excerpt is redacted before it is stored like every other tool payload.

## Source workspace boundary

Beside the excerpt reader, the same monitored checkout can be copied into a disposable workspace so
that a later change can be prepared against a fixed base. The copy is a filesystem primitive with no
host wiring yet: nothing registers it, no tool exposes it and no model input reaches it.

**What it reads.** Only the configured monitored root, and only for reading. The checkout is never
opened for write, moved or renamed, and the production mount stays a read-only bind. The walk
rechecks containment below the canonical root for every entry, against the same path boundary the
source-lookup path uses rather than a second one written for it, and refuses the whole tree rather
than skipping an entry when it meets a symlink, junction or any other reparse point on a file or a
directory, a `.gitmodules` file at any depth, or a `.git` entry below the root's own. The root's own
`.git` is skipped rather than copied, so a workspace holds working-tree content and no repository
history. Submodule refusal is a marker heuristic, not git semantics: nothing here runs or links git,
so a declared-but-absent submodule and a merely nested independent repository are refused alike.

**What it writes.** One freshly named directory below a configured workspace root, and nothing
outside it. A workspace root that is the monitored root or sits below it is refused before anything
is created. File count, total bytes and tree depth are bounded, and exceeding any bound refuses the
materialization and deletes the partial copy rather than returning a truncated tree: a truncated
copy would carry an identity for a tree that exists nowhere. The directory is removed on every
terminal path, refusal, filesystem error, cancellation and disposal alike. A killed process is the
one case deletion cannot cover, which is why the workspace root is operator-configured and why
prefixed leftovers below it are reapable.

**What it executes.** Nothing. No process is started, no file in the copy is opened again after it
is written, and the copy is inert bytes until some later caller reads it. The architecture test that
fails the build when `System.Diagnostics.Process` appears in the Application project is unchanged
and remains a forward guard.

**What the identity proves.** Each workspace records a tree identity: a SHA-256 over the copied
files, each contributing its repository-relative path and the SHA-256 of its exact bytes, ordered by
path and length-framed under a versioned domain separator. File bytes are not normalized, so two
checkouts of the same upstream commit under different line-ending settings are two different bases
and carry two different identities. Equal identities mean the same paths with the same bytes under
the same admission rules, which is what lets a later reader ask whether the base a change was
prepared against still exists. It is not a commit id: nothing here reads git, so the identity says
nothing about which commit, branch or upstream repository the tree came from, and it covers no file
mode, ownership, timestamp or empty directory.

**What is not logged.** Absolute host paths, workspace paths and file contents stay out of logs and
durable state, exactly as they do for the excerpt reader. A refusal surfaces a code from a closed
vocabulary and nothing else.

## Source patch boundary

A unified diff can be parsed, validated and applied inside one of those workspaces. Like the
workspace itself this is a primitive with no host wiring yet: nothing registers it, no tool exposes
it, and no model output reaches it. It is written for the day one does, so it treats the diff as
hostile input: a patch is model text, and the model that wrote it was shown incident data an
attacker may influence.

**Nothing is executed.** No process is started, no file in the workspace is opened again after it is
written, and no command, script or hook in the tree is run. The architecture test that fails the
build when `System.Diagnostics.Process` appears in the Application project is unchanged.

**What is accepted.** A small subset of the unified-diff format: an optional `diff --git a/<path>
b/<path>` line, an optional `index` line, an optional `new file mode 100644` or `deleted file mode
100644`, a `--- a/<path>` or `--- /dev/null` line, a `+++ b/<path>` or `+++ /dev/null` line, and one
or more `@@ -start,count +start,count @@` hunks whose bodies use `' '`, `'-'` and `'+'` origins and
the `\ No newline at end of file` marker. That is the whole language. Both sides of a section must
name the same path, so a section either modifies, creates or deletes exactly one file.

**What is refused, and when.** Refusals fall into three groups, and each is refused as early as it
can be, so a refusal never depends on more than it has to.

Refused from the text alone, before any file is opened:

- a patch that is blank, describes no file, or has a section with no hunks;
- a patch larger than the raw budget below;
- a patch holding an unpaired surrogate, which denotes no character and so is not text any file
  could hold;
- a path that leaves the workspace or means two different things on two platforms: `..` or `.` as a
  segment, a leading `/`, a `//` UNC prefix, a rooted path or a drive letter, a backslash, a colon
  (which is both a drive separator and an NTFS alternate data stream), an empty or trailing segment,
  or a segment ending in a dot, which Windows strips when it opens the file;
- a path holding anything outside printable ASCII, `U+0021` to `U+007E`. The reason paths are
  checked at all is that a human approves a diff by reading it, so the path a reviewer reads must be
  the path a filesystem opens. A NUL truncates the name for anything reaching a C string; control
  characters and spaces hide or move what follows them; *format* characters, which `char.IsControl`
  does not cover, are worse than invisible, since a bidirectional override reverses the segment a
  reviewer sees while leaving the bytes that are opened untouched (the Trojan Source shape) and a
  zero-width space or byte-order mark is a segment boundary nobody can see; and fullwidth forms are
  homoglyphs of the separator, the escape and the drive separator. Refusing the whole range refuses
  the next homoglyph too. The cost is a repository whose file names are not ASCII, which this cannot
  patch, and that is a deliberate trade;
- a path holding one of `* ? " < > |`, which Windows rejects in a file name, refused on every
  platform for the same reason the device names are;
- a path naming a Windows device (`con`, `nul`, `com1` and the rest), refused on every platform so
  that a patch cannot be admitted on Linux and refused on Windows;
- a path deeper than the tree walk admits. Depth is a property of the text, so it is refused from
  the text rather than by the walk that recomputes the identity afterwards, which would mean writing
  the file, refusing the tree it produced and rolling the whole attempt back;
- a path with a `.git` or `.gitmodules` segment, so a patch cannot write repository metadata,
  declare a submodule, or reach into a nested repository the workspace already refused to copy;
- a path that looks like key material or a credential file: `.env` and its suffixed forms, `.netrc`,
  `.npmrc`, `.pgpass`, `.git-credentials`, `id_rsa` and the other private-key names, anything under
  `.ssh`, `.aws`, `.gnupg` or `.docker`, and the `.pem`, `.key`, `.pfx`, `.p12`, `.cer`, `.crt`,
  `.der`, `.jks`, `.keystore`, `.ppk` and `.asc` extensions. The list is specific and short: it is
  not a general secret detector and is not the reason a patch is safe;
- a path whose extension the source-read boundary would not read back. The extension set and the
  size bound are read from the same options the excerpt reader runs under rather than restated, so
  lowering either in configuration also lowers what a patch may leave behind and no change is made
  that the evidence path could not then quote;
- a rename, a copy, a mode change, a create asking for an executable file (`100755`), a symbolic
  link (`120000`) or a submodule gitlink (`160000`), each refused by name rather than skipped, since
  a parser that ignored the header would read a rename as a plain write to the destination;
- a binary patch, whether announced as `GIT binary patch` or as `Binary files ... differ`;
- a hunk whose body holds a different number of lines than its header declares, whose origin
  character is not one of the three, whose body line is empty rather than a single space, or whose
  no-newline marker is misspelled, comes before any line it could attach to, or is followed by
  another line on the side it closed;
- a hunk header naming a base line beyond what an admissible file could hold. A file the patch may
  touch is bounded, and a file of *n* bytes holds at most *n* lines, so a larger start describes a
  file this would refuse to open. It is also what keeps every later sum of a start and a count
  inside a 32-bit integer: an unbounded start makes `start + count` wrap negative, and a negative
  offset walks off the front of a list rather than being caught by a bound written to catch walking
  off the end;
- two hunks of one file that cover the same base lines or run backwards, and a hunk header whose
  result start contradicts what the hunks before it added or removed;
- two sections naming the same path, compared case-insensitively on every platform. Their line
  numbers would be ambiguous, since the second could address the base or the file the first
  produced, and the format does not say which;
- two sections where one path is a directory prefix of the other, compared the same way. Such a
  patch asks for one name to be a file and a directory at once, which is coherent in one order and
  destroys a file in the other, and the format does not say which order applies;
- a patch changing more files, or a section carrying more hunks, than the bounds admit.

Refused once the base can be read, still before anything is written:

- a target reached through a symlink, junction or any other reparse point;
- a path segment that differs from the name the workspace holds only in case. `File.Exists` answers
  a different question on each platform, so it cannot be the whole of a lookup that has to mean one
  thing: with `lib/a.cs` in the workspace, a section naming `lib/A.cs` modified the existing file on
  Windows and was refused as missing on Linux, and a section creating it was refused as existing on
  Windows and produced a second file on Linux. On Windows the record was wrong as well as divergent,
  since the plan named the path the patch wrote while the identity walk afterwards reported the name
  the disk held. The comparison is therefore made explicitly and in the same direction everywhere:
  an entry matching the segment exactly is the segment, and an entry matching it only ignoring case
  is a refusal wherever the worker runs. It is the same commitment the duplicate-path rule makes
  about the patch, applied to the workspace;
- a modification or a deletion of a file the workspace does not have. Treating a missing delete
  target as already done would let a patch claim to have removed something it never saw;
- a creation of a file the workspace already has. A create quotes no base line, so overwriting there
  would be the one way a change could reach a file without passing the context check at all;
- a target that is not decodable text, by the same NUL and strict-UTF-8 test the excerpt reader
  applies;
- a target larger, before or after the change, than the excerpt reader would open;
- context or removed lines that do not match the base byte for byte at exactly the offset the header
  named. There is no fuzz, no offset search and no whitespace tolerance: a patch that does not match
  where it claims to match was generated against a different base;
- a hunk that would change whether the file ends with a terminator without saying so, including a
  patch that appends to a file that ends without one. A carriage return is part of a line's bytes, so
  a patch generated against a checkout with one line ending does not apply to a checkout with the
  other, which is the same rule the tree identity follows;
- a no-newline marker on a hunk that stops short of the end of the file, on whichever side carries
  it. The marker is a claim about a file's last byte, and a hunk that does not reach the last byte
  is in no position to make one. Whether a hunk reaches the end is a fact about the base rather than
  about the text, which is why this one refusal about the marker sits here and the rest sit above;
- a deletion whose hunk did not quote the whole file.

**What a body line may hold.** None of the path rules above apply to a body line. A body line may
carry a bidirectional override, a zero-width space, a byte-order mark, or anything else the file it
quotes may carry, and that is deliberate. Its whole job is to be the file's exact bytes, which is
what the context check compares and what the tree identity is computed over; filtering it would make
an ordinary file with a byte-order mark unpatchable. The two cases also differ in what goes wrong. A
hostile path makes a reviewer approve a change to a file they did not see, which nothing downstream
can recover from, so it is refused. A hostile body line makes a reviewer misread code whose bytes are
exactly the bytes that land, which is a rendering problem and an obligation on the boundary that
shows a diff to a human, a boundary that does not exist yet: **it must render format and
bidirectional characters visibly.** Refusing them in the parser would also be theatre, since a patch
can hide meaning in ways no parser can judge.

**The raw budget.** The action-payload ceiling is 64 KiB of *canonical JSON*, not of patch text. The
canonical writer escapes every non-ASCII character and several ASCII ones, `<` and `>` among them, to
a six-byte `\uXXXX` form, so one raw byte can become six and no sequence does worse. Reserving 1 KiB
for the fields that travel beside the patch, and two bytes for the quotes around it, leaves
`(65536 - 1024 - 2) / 6 = 10751` raw UTF-8 bytes. A remediation patch is a small targeted change or
it is refused, and a unit test carries a worst-case patch of exactly that size through the same
canonical writer the approval contract uses to prove the arithmetic rather than assert it.

**All or nothing.** Every decision that can be made against the base is made before the first byte is
written, so a patch that fails on its fifth file is refused with the workspace untouched. Writing is
then the only step that can still fail, for reasons no inspection predicts, and the first failure
undoes the commit: it deletes what was created, removes the directories the commit made, and only
then puts back what was replaced. The order is not incidental. A commit can delete `lib/A.cs` and
then create `lib/A.cs/C.cs`, which makes that name a directory; restoring files first would write
`lib/A.cs` onto a directory, fail, and then remove the now-empty directory, leaving nothing at all
where a file used to be while the caller was told the patch was refused. The parser also refuses a
patch whose paths nest like that, which is the better place to refuse it, but the rollback does not
depend on that rule holding.

**A rollback that failed says so.** Restoring is best effort, because a filesystem that refused a
write may refuse the write that undoes it. Every step therefore checks what it left behind rather
than assuming a caught exception meant nothing changed: a creation is undone when the path holds no
file, a replacement when the file holds the bytes it held. When any step cannot get there the
outcome is `source_patch_rollback_failed` rather than `source_patch_unavailable`, because "nothing
was applied and the workspace is as it was" and "nothing was applied and the workspace is something
else" are different things to tell a caller. On the second, the workspace must be discarded rather
than read, identified or reused. A cancelled attempt reports neither, since a cancellation carries no
outcome, so a caller that cancels must discard the workspace as well.

**Discarding the workspace, and what that does not cover.** The workspace is disposable, so a caller
that sees a refusal it did not expect can discard the whole directory, which is the guarantee that
does not depend on the filesystem cooperating. Nothing is written outside the workspace, with two
qualifications. The checks that establish that are made when the plan is built and the writes happen
afterwards, so a workspace that something else is changing underneath is outside what they promise; a
concurrent writer inside the workspace is outside the threat model rather than impossible. And the
workspace root itself is never link-resolved: every segment below it is checked for reparse points,
but if the root a host configured is reached through one, discarding the directory remains all a
caller can do, and where those bytes physically live was decided by the host's configuration rather
than here.

**What the caller must check that this does not.** A hunk that consumes no base line quotes no base
line, so it matches at its offset in *any* file: nothing in an insert-only patch binds it to the tree
it was generated against, and the context check has nothing to compare. The base tree identity is the
designed answer to that, and enforcing it is the caller's obligation, not the applier's. **A caller
applying an approved patch must compare the identity of the workspace it is about to change against
the identity the patch was approved for, and refuse when they differ.** Without that check, an
approved diff can be applied to a tree its approver never saw.

**What the result identity proves.** After a patch applies, the workspace is walked again and its
tree identity recomputed from the bytes on disk, under the same admission rules the base identity
used, rather than derived from what the applier believes it wrote. A record can therefore carry the
base a change applied to and the result it produced, and both are statements about a tree that
existed. What the identity does not prove is unchanged from the workspace boundary above: it is not a
commit id, and it says nothing about whether the change is correct, builds or passes anything,
because nothing here runs a test.

## Remediation diff boundary

A post-report remediation pass puts the two primitives above behind one bounded operation: it names a
base, asks a model for a unified diff, applies that diff to a copy of the base, and records what it
produced. Unlike the primitives it composes, this one is wired: an Application port, a registered
local adapter, a durable table, a model call and a trigger.

**What turns it on, and what runs it.** Publishing a report writes a post-report action intent for
`remediation_diff`, and the Worker's post-report evaluation loop runs the pass from that intent under
the fenced claim, attempt cap and dead-lettering that loop already owns. Nothing runs a pass on an
API request thread. Whether an intent is written at all is the same switch every external action
uses, in the reviewed triage configuration: the tool declared with category `code_write` and logical
target `source:configured-workspace`, listed in `Actions.AllowedTools`, and neither its own `Mode`
nor `Actions.DefaultMode` set to `disabled`. The shipped configuration declares it disabled and
grants nothing, so no pass runs until an operator changes both. The switch is evaluated twice, once
when the intent is written and once when it is claimed, so turning it off stops an enqueued pass
before it spends anything. It is a deliberate governed capability rather than a tool a model may
call: nothing registers it as an agent tool and no role can be granted it.

**What the host must also supply.** Two host options together, and neither has a default. A monitored
root for the exact `(service, release)` the fault selects, and
`IncidentCompass:SourceContext:WorkspaceRoot`, the absolute directory disposable copies are created
below. With either missing the pass refuses with `remediation_not_configured` before it calls a
model, touches no filesystem and spends nothing. There is deliberately no default workspace root: a
default would make the first host with a monitored checkout start writing copies of it somewhere
nobody chose. The configuration switch and the host options are independent on purpose: a tenant
decides whether a fix may be prepared, and a host decides whether this machine has a checkout and the
room to copy it.

**What the model is shown.** The grounded report's classification, confidence, summary, limitations
and recommended next action, plus the `SourceCode` artifacts the investigation actually cited, each
already redacted on its way into durable state. All of it sits inside the same untrusted-context
boundary the investigation prompts use, using the same marker strings rather than a second spelling
of them. Every part is bounded: at most sixteen evidence items, each excerpt capped, the narrative
fields capped, the limitations capped in count and length. The service, the release and the base
identity are backend-selected and stated; the model names no file, no path and no release, and there
is no tool surface on the call at all.

**What the model may answer.** One unified diff and nothing else, bare or inside a single ```diff
block with nothing but blank lines around it. A sentence before the fence, a sentence after it, two
blocks, a block of something else, an apology or an empty answer are all refused as
`remediation_answer_not_a_patch`. The recovered text is passed through byte for byte: carriage
returns are not normalized, whitespace is not trimmed inside the block, no header is inferred and no
hunk count is corrected, because the bytes that are parsed and applied must be the bytes a reviewer
reads. Everything past that shape check is the patch boundary above, unchanged.

**What a refusal costs.** A refusal the model could fix, an answer that is not a diff or a diff the
backend refuses on its own terms, is worth one correction, bounded by the configuration's own
`MaxReprompts` and durably visible as a bounded `BudgetEvent` with the `remediation_patch_reprompt:`
prefix. The correction carries the closed outcome code and nothing else: no path, no line, no byte of
the diff the model sent and no byte of a file. A refusal about the environment, a base that moved, a
filesystem error or a rollback that failed, is not reprompted, because no answer fixes it. The
adapter decides which is which; a caller guessing from the shape of a code string would guess wrong
the first time the vocabulary grew.

**The base obligation, discharged.** The previous section states it as the caller's: a hunk that
consumes no base line matches at its offset in any file, so an insert-only diff binds to no tree and
the context check has nothing to compare. The pass names the base before the model is asked, states
it in the request, and hands it back into the apply, where the adapter compares it against the copy
it just materialized and refuses with `remediation_base_mismatch` before the diff is even parsed. A
checkout that moved between naming the base and applying the diff is therefore caught, not assumed
away, and the same identity travels on the record so a later application of an approved diff has
something to compare. A mismatch is never reported or corrected as a patch problem.

**Where the diff body lives.** In `incidentcompass.remediation_diffs.patch_text`, and nowhere else.
Not in a log line, not in a ledger rationale and not in a refusal code, all of which carry closed
vocabulary codes and no content. That table is separate from `triage_artifacts` on purpose: every
payload in `redacted_payload` passes a redactor on its way in, which is what makes that column safe,
and a diff cannot pass one and remain a diff, since a redacted context line no longer matches the
base and a redacted added line writes a placeholder into source. Putting a diff there would mean
either breaking it or exempting it, and an exemption inside the redaction boundary is worse than a
table outside it. Rows there are model text about attacker-influenced incident data and every reader
must treat them as such.

**What a record carries, and what it cannot.** The tenant, the report and job it came from, the
service and release, the base identity it applied to, the identity it produced, how many files it
touched, its own byte count, the diff, the route and model that wrote it, and the outcome. It is
insert-only. Every field is bounded: the identities are fixed-width digests, the counts are integers,
and the only free text is the diff, whose size the database refuses above the raw budget the
action-payload ceiling leaves for it. A row exists only for a diff that applied whole, so a refused
attempt cannot be stored and later mistaken for evidence.

**No test is executed.** Nothing in this pass starts a process, and the architecture test that fails
the build when `System.Diagnostics.Process` appears in the Application project is unchanged. Every
record therefore carries `test_outcome = 'not_executed'` and a null `test_command_id`, and the schema
enforces both rather than trusting a writer: a later release that runs a test has to relax those
checks in its own migration, so claiming a test ran cannot be done quietly. A remediation diff is a
change that parsed, matched its base and applied. It is **not** evidence that the change builds,
passes anything, or is correct.

**What a killed process leaves behind.** The pass disposes its own copy on every terminal path, so a
leftover needs a crash between the copy and the delete to appear. The Worker's retention pass deletes
those as a third bounded operation: prefixed directories under the configured workspace root whose
own name states an instant older than `IncidentCompass:SourceWorkspaceRetention:RetentionHours` and
which have not been written to since. It deletes nothing else, never touches the monitored checkout,
and leaves alone any directory it cannot date. The window is six hours by default against a live
workspace that cannot outlive one bounded copy-and-apply call, because a leftover surviving an extra
pass costs disk while a workspace deleted under a running pass costs a refusal that looks like a
filesystem fault.

**What is still missing.** An approval path for a produced diff. A record is a statement for a human
to read; nothing applies it to the monitored checkout, pushes a branch or opens a pull request.

## Redaction And Pseudonymization

Redaction runs at two boundaries, not one. Incoming signals are redacted during intake, before the
signal row is written. Everything a worker tool reads from a connector afterwards is redacted again on
its own way to durable state, because that text never passed through intake. Both boundaries use the
same rules; the rest of this section describes intake first and the tool boundary second.

### Intake

Built-in secret patterns remain active for every signal. The triage config can add attribute-key
redaction and bounded .NET regular-expression replacements before persistence and before model calls.
These rules are defense in depth, not a guarantee that every possible secret or PII shape is known.

A built-in property-name denylist redacts values whose JSON property name looks like a secret holder,
independently of the configured attribute keys. The rule an operator can predict: a property name is
lowercased and split into segments on every non-alphanumeric character and on camel-case and
letter/digit boundaries, and the value is redacted when any run of consecutive segments joins to one
of `accesskey`, `apikey`, `authorization`, `clientsecret`, `connectionstring`, `cookie`, `jwt`,
`passwd`, `password`, `privatekey`, `secret` or `token`; the value is also redacted when the whole
name, with separators removed, equals the whole-name-only term `session`. So `x-api-key`, `Cookie`,
`Set-Cookie`, `Authorization-Bearer`, `user_password_hash`, `db_password_2`, `access_token` and a
property named exactly `session` are redacted, while `session_id`, `sessionCount`,
`session_start_time`, `keyword`, `key_count` and `totalTokens` are not. Broad words are whole-name
terms precisely so that ordinary incident context is not destroyed; the trade-off is that a name such
as `token_count` is redacted and a plural such as `cookies` is not. Like the configured rules, this
matcher is best effort (see `docs/trade-offs.md`).

Configured patterns are compiled once per configuration snapshot and each match runs under a 200 ms
timeout. A pattern that exceeds it fails closed: the entire field is replaced with the distinct marker
`[REDACTED:PATTERN_TIMEOUT]`, never with the partially processed intermediate value, so a hostile
input cannot pass through a redaction rule that did not finish and cannot abort intake either. The
timeout is logged once per pattern per configuration snapshot with the pattern name and the field
path only. The field value is exactly the text redaction failed to clean, so it is never logged.

Configured user-identifier attributes are replaced before redaction with an HMAC-SHA256 pseudonym.
The salt comes only from host secrets or `IncidentCompass__Pseudonymization__Salt`; it is not stored in
the triage config, config snapshot, artifact or ledger. If the salt is absent, identifiers fail safe to
`[REDACTED]`, so distinct-user continuity is unavailable but raw identifiers are not stored. Rotating
the salt changes every pseudonym and breaks counts across the rotation boundary.

Pseudonymization is an intake-only step. It needs the configured user-identifier attribute paths of a
normalized signal, and a memory chunk, ticket field or source excerpt has no such paths.

### Worker tool artifacts

A worker tool reads from incident memory, the local source root or a ticket provider. That text is not
signal data and never passed through intake, so it is redacted where it becomes durable instead. One
place does it, on the same governed path every immediate tool call takes:

- the tool returns its payloads as drafts, not as artifacts, so a tool has no way to write a row
  itself and no way to skip this step;
- the worker tool executor redacts the model-visible tool output and turns each draft into an
  artifact, redacting the payload, canonicalizing it and hashing the redacted form, so
  `triage_artifacts.content_hash` describes the bytes that were actually stored;
- the delegated worker's own output takes the same path before it is stored as a `WorkerOutput`
  artifact, because that text quotes the tool results the worker was given, and the delegate result
  the orchestrator receives is built from the stored payload rather than from the worker's raw text;
- a failed tool's error message is redacted with the same rules before it becomes this turn's tool
  message and a `ToolResult` ledger rationale.

Both surfaces have to be covered together: the same connector text reaches the model once as this
turn's tool message and again through the stored artifact, which the orchestrator reads back into
later prompts. Redacting only the row would leave the immediate turn unprotected.

### Saying so in the report

A published report states when the evidence behind it carried a value redaction removed, so a reader
of the conclusion knows part of the input was withheld from the model too. The sentence is the
backend's, appended at publication the same way read-only context outcomes are, and it is owned in
both directions: appended when the backend derived the marker, and dropped from the model's own
limitations when it did not.

What that owns is the exact reserved wording, not the idea. The removal is an ordinal string
comparison, so a model-authored paraphrase - a different case, an extra clause - is not the reserved
sentence and is left standing as one of the model's own limitations. That is deliberate. The
direction that matters needs no matching at all: when the backend derives the marker it appends the
sentence, so nothing the model writes can suppress a real withholding. A looser match would only
remove near-copies claiming a withholding that did not happen, and it would pay for that by deleting
report text on a fuzzy comparison, which is the one direction that can destroy a genuine limitation
a human needed to read. So the guarantee is: the reserved sentence appears exactly when the backend
derived the marker. A sentence that merely reads like it is model text, and carries no more standing
than the rest of the model's limitations.

Where the answer comes from matters more than the sentence. The stored payload cannot answer it: a
value the redactor replaced and connector text that already spelled out `[REDACTED]` are the same
bytes, so a marker derived by searching payload text for that literal would be one the author of a
ticket or a source file could raise at will. The answer is recorded instead by the tool redaction
boundary, in `triage_artifacts.redaction_applied`, from a comparison against the pre-redaction
document at the one moment the two are distinguishable. Publication reads that column for the
artifacts the report cites.

Both citable artifact kinds the boundary produces record it: the per-item `RetrievedItem` artifacts a
tool hands back as drafts, and the `ToolResult` artifact built from the same call's redacted output.
That pairing is the point. They carry the same redacted text and ground equally well, so if only one
of them recorded an outcome the model would choose whether the limitation appeared by choosing which
of the two to cite.

For the same reason the read is not capped. Every parsed citation is looked up, because the model
authors and orders its own evidence array: an answer covering only part of that array is one the
model can steer, and truncation can only ever steer it towards saying nothing was withheld.

Its scope is exactly what that boundary can see. `true` means the redactor changed the payload,
`false` means it ran and changed nothing, and NULL means no boundary recorded an outcome for the
row - which is what intake-written artifacts carry, because they are assembled from a signal intake
had already redacted before the artifact existed, and what the post-report action artifacts carry,
because no redaction pass runs on their backend-derived payloads. Only `true` contributes, so the
marker's absence says no cited artifact is known to have been redacted, not that nothing was.

Redaction operates on the parsed JSON document, rewriting values and rebuilding objects and arrays
node by node. A redacted payload is therefore still valid JSON with the same keys. Value kinds
survive the pattern rules, which only rewrite strings, but not the property-name denylist: a value
whose property name looks like a secret holder is replaced with the string `[REDACTED]` whatever its
original kind was, so `{"tokenCount": 42}` is stored as `{"tokenCount":"[REDACTED]"}`. That is
deliberate. A field named like a secret is redacted whether it arrives as a string, a number or an
object, because the alternative is a rule that a caller can defeat by changing the value's type.
Nothing downstream reads a tool or worker payload as a typed value except two SQL reads, and both are
guarded by `jsonb_typeof`: `isMassIssue` on a `NeighborSet`, a kind this boundary does not write, and
`score` on a citable evidence artifact, a kind it does. Neither name is on the denylist, and a value
that had been flattened would read back as NULL rather than fail the query.

The pass is idempotent: running a redacted payload through it a second time changes nothing. That is
a property worth having because the same connector text is stored twice, once as a `RetrievedItem`
and again inside the `ToolResult` payload, and because a redacted document is what the worker output
path hands to the orchestrator after storing it. It is not what protects re-triage: the re-triage
scheduler copies `redacted_payload` and `content_hash` forward verbatim and never re-redacts, and it
copies only intake-written kinds.

Two limits are worth stating plainly. The property-name denylist sees different keys depending on
which payload it is applied to. A tool payload's keys are backend-authored (`excerpt`, `title`,
`quote`, `url` and so on), so the denylist contributes little there and the pattern rules do the
work. A `WorkerOutput` payload's keys come from model-authored JSON instead, bounded by the role's
configured output schema, which is what makes the type-flattening above reachable on a name the
backend did not choose. And source excerpts get no exemption: `source_lookup` returns application
code, the same rules run over it, and a line containing `password =` or `pwd =` loses its right-hand
side while the rest of the excerpt survives. That cost, the analysis behind it and the bound that
keeps it to a single line are recorded in `docs/trade-offs.md`.

## Payload Retention

Two lifecycle operations shorten how long raw payloads stay readable. Neither is data governance, and
neither deletes a record: they empty or drop payloads, and everything that makes a record a record is
left where it is. Both are bounded per run, idempotent and safe to interrupt, because each is a single
statement whose predicate is the only state it keeps.

The Worker runs both, one bounded pass of each every fifteen minutes; no API request triggers either,
and no operator action is required to keep them running. Two things follow for anyone reasoning about
exposure from this section. The windows are a promise about a running Worker only: a deployment whose
Worker is stopped, or whose `IncidentCompass:RetentionSchedule:Enabled` is set to `false`, keeps every
raw payload readable for as long as it stays that way, and turning retention back on drains the
backlog at a bounded rate rather than instantly. And a window is an upper bound on age, not a
deletion deadline: a pass compacts at most `MaxRowsPerRun` rows, so a payload past its window stays
readable until a pass reaches it. `docs/single-host-production.md` covers the operational side.

**Aged signal payloads are compacted, not deleted.** `signals.attributes` and `signals.body` are
emptied for signals intake received longer ago than the configured window. The signal row itself
cannot go: `faults.trigger_signal_id` references it, so removing the row would take the trigger away
from the fault the signal opened. Everything the pipeline derived from the payload before storing it -
fingerprint, service, environment, severity, error type, summary, trace ids, the fault link - is
untouched and stays readable.

An operator reading such a row afterwards has to be able to tell an emptied payload from one that
arrived empty, and the payload cannot answer that: both are the same `{}` bytes, and a sentinel
written inside the payload would be something a connector could write too. So the claim is a column,
`signals.payload_compacted_at_utc`, written by the backend at the moment it empties the row. NULL
means retention never touched this row, which is deliberately not the same claim as "arrived empty".

Ageing is by `received_at_utc`, never by `observed_at_utc`. The observed time arrives inside the
signal, so a source that chose it could have its payload dropped immediately by naming an ancient
time, or stay out of retention forever by naming a future one. The received time is written by intake.

**Compaction does cost something, and it lands on a late re-triage.** The derived fields survive, but
two worker tools read the raw payload directly and get less after it is emptied. `source_lookup`
parses a stack trace out of `attributes["exception.stacktrace"]`, `attributes["exception.stack_trace"]`
or `body["stackTrace"]` before falling back to the signal's `description` and `error_message`
columns, which retention leaves alone; once the payload is gone, a signal whose trace lived only in
the payload yields no frames and the tool returns `source_frames_not_found`. `ticket_search` reads
`attributes["service.component"]`, `attributes["component"]`, `attributes["code.namespace"]` and
`attributes["incident.labels"]`; after compaction it searches on fingerprint, service, error type and
error message alone. Neither fails - both degrade to a narrower answer - and neither affects a job
that ran while the payload was still there, because the artifacts of that run are stored separately.
The exposure is a job claimed after its signal's window has expired: a re-triage of an old fault, or
a job that sat unclaimed longer than the window. An operator shortening `SignalPayloadRetentionDays`
is choosing how far back a re-triage still gets full source and ticket context.

**Artifacts of non-current attempts are reaped.** This is worth stating precisely, because it is less
than it sounds like: a failed attempt is not a fact this system records. A retry that does not consume
an attempt reuses the attempt number, so the working evidence of a run that went wrong and of the run
that replaced it are indistinguishable. The only implementable predicate is "this artifact does not
belong to its job's current attempt", and that is what runs. Reusing an attempt number keeps both
runs' artifacts, which is the safe direction.

An age threshold applies on top of the attempt predicate, and it is not decoration. An attempt stops
being current the moment the next one is claimed, so reaping on the attempt predicate alone would
destroy the artifacts of the attempt that went wrong at exactly the moment an operator would come
looking for them.

Nothing a report cites is ever reaped, and neither is anything a governed action still points at. The
exclusions are: job-level artifacts (the `attempt IS NULL` sentinel, which intake writes for facts
that stay valid across retries); the job's current attempt; the `ProposedAction` and `ActionResult`
kinds, which are the audit record of a governed external action rather than working evidence; any
artifact cited by `triage_evidence`; any artifact that is an approval's proposal artifact; and any
artifact named by `action_approval_provenance`. The last one is the one that needs stating: its
`source_id` is polymorphic over reports and artifacts, so it carries no foreign key at all, and the
database would neither refuse the delete nor report the orphan afterwards. That exclusion is the only
guard that exists.

**Reports and the audit ledger are out of scope, and that is structural.** Published reports are
immutable by trigger: `trg_triage_reports_immutable` rejects UPDATE and DELETE unconditionally, so
report retention is not unimplemented, it is refused by the schema. The triage ledger is the audit
trail these operations are meant to leave intact, so nothing removes ledger entries either.

The ledger survives a reaped payload and says so. It stores a compact reference, `payload_ref`, as
plain text in the shape `artifact:{id}` with no foreign key, and the ledger reader resolves that
reference as a scalar expression rather than a join, so reconstruction shows the same audit sequence
before and after retention has run. Each event also carries a `payloadState` answered at read time -
`Retained`, `Reaped`, `NotReapable` or `None` - because surviving retention and being honest about it
are different properties: without that field a reaped reference and a live one render as the same
string and a reader cannot tell which it is holding. See `docs/observability.md`, "Reading a timeline
after retention", for what backs each value.

**Retention cannot mutate the compact external-action audit projection.** The four
`external_resource_*` columns on `action_approvals` are the durable record of what a governed action
did outside the system, and neither retention operation can reach them. Compaction writes only to
`signals`; the reap deletes only from `triage_artifacts`, excludes the `ProposedAction` and
`ActionResult` kinds by name, and cannot cascade into an approval because `proposal_artifact_id` is a
plain foreign key with no `ON DELETE` action. Underneath that, the table refuses the mutation
outright: `trg_action_approvals_no_delete` rejects every DELETE, and the lifecycle trigger rejects
any UPDATE of those four columns outside the single approved-to-executed transition that first sets
them. So the guarantee does not depend on the current retention predicates staying as they are.

## Tools

Unknown, unregistered, ungranted and invalid worker tool calls fail closed with audit-visible decisions.

That is the rule the rest of this section describes. Tool execution must go through backend policy,
risky tools require approval or must be rejected, and the LLM must not receive infrastructure
credentials.

The investigation loop gives the orchestrator only backend-owned `delegate` and `publish_report`
actions; `delegate.role` is generated from configuration and validated again before execution.
Worker-tool proposals are recorded as `ToolProposed`, checked against role grants and ledger-backed
rules, recorded as `PolicyDecision`, and only allowed backend calls execute.

The shared rule engine also fails closed on the rules themselves rather than skipping what it cannot
evaluate:

- a rule whose type it does not recognise is denied (`unknown_rule_type`);
- a `precondition` rule that names no prerequisite tool is denied
  (`precondition_missing_prerequisite`);
- a `rate_cap` rule without a positive maximum is denied (`rate_cap_missing_max`).

Every denial names its cause the same way: a stable reason code, optionally followed by `: ` and a
human detail, with the code always the leading token of the recorded reason. The post-report denial
vocabulary is derived from that code rather than from the wording after it, so rephrasing a denial
message cannot change which denial an operator or a read model sees.

The same engine decides immediate reads and post-report action proposals, so both paths deny
identically. Load-time configuration validation rejects the same malformed rules earlier, but the
engine does not depend on having been called with validated configuration.

Immediate reads and external actions are separate backend capabilities: role grants accept only
immediate tools, `Actions.AllowedTools` accepts only exact registered external tools, and external
actions are never advertised to the investigation model. Startup and `config validate` reject
capability mismatches, wildcard or read-tool approval targets and action metadata that disagrees
with registration.

Report publication is also fail-closed. The model may name evidence references, but the backend
accepts only citable artifacts from the same job/current attempt, never `WorkerOutput`, derives
evidence kind and `is_mass_issue` itself, and marks prior reports as untrusted hypotheses in the
artifact payload.

The report's own account of which models produced it is derived the same way. `model_provenance` is
read inside the publish transaction from that attempt's `ModelCall` ledger rows, which record what
actually answered rather than what the route asked for, so a `publish_report` body that asserts its
own provenance changes nothing about what is stored. See `docs/observability.md`, "Report Model
Provenance".

The durable post-report evaluation queue is backend-owned. Report publication selects only startup-
registered workflows whose exact tool id, workflow version, category and logical target match the
external-action registry, then commits their intents atomically with the report. Canonical workflow
input contains only the origin report id, tool id, version and optional bounded route id. It cannot
carry report or evidence text, model output, provider input, credentials or an adapter-selected
destination. Invalid input, missing exact catalog membership and exhausted attempts fail closed.
Evaluation may invoke only the existing governed proposal use case; it never receives adapter authority.

Post-report proposal creation is backend-owned and starts only from a current immutable published
report and its exact configuration snapshot. The registered tool selects category, logical target
and binding; arguments cannot replace them. Global disabled mode denies, dry-run cannot be loosened,
and every non-notification write requires approval. The shared rule engine reads external preconditions
and accepted-proposal caps only through the fault-first proposal transaction after its current-origin
and job recheck. Registered and configured tool ids use one bounded case-sensitive safe grammar. A
safe-origin denial records only a closed reason code; input rejected before same-tenant origin resolution writes nothing. Proposal creation never
invokes the external tool adapter.

Approval decisions submit the exact observed payload and approval hashes. A stale lifecycle state,
expiry, hash mismatch or superseded origin conflicts without approval. The public review surface
contains the frozen safe tuple, canonical payload and backend-derived provenance only. It excludes
adapter binding inputs, credentials, raw routes, prompts, transcripts and evidence bodies. No current
API endpoint edits payloads, dispatches an action or retries an outcome.

Approved action dispatch is a separate Worker path. The Worker rechecks current policy only as a
tightening guard, recomputes the registered adapter-binding fingerprint and passes the immutable
stored bytes plus the action id only to the exact registered external-action capability. Frozen or
newly tightened dry-run performs no adapter call; disabled, approval-tightened, unregistered and
binding-drift cases fail closed with bounded durable evidence. Application composition registers
only the non-secret Telegram, ticket-create and ticket-update descriptors for public configuration
validation. Their workflows, adapters, host bindings and credentials are registered only in the
Worker host.
API and shared test host configuration do not require or receive provider credentials.

Confirmed live action results may expose one compact external-resource projection through approval
list/get responses. The projection uses a closed resource kind, a positive decimal provider identity
and a closed bounded before/after marker. It contains no token, recipient, repository authority,
request route, provider body, prompt or report text. Exact list lookup requires kind and id together,
uses a tenant-leading partial index and always applies the authenticated server-owned tenant. A
foreign resource identity therefore returns the same empty list shape as an absent identity.

The durable claim is the at-most-once boundary. Once it records an owner, random fence and database
deadline, no automatic path may call that adapter again. Exceptions, timeout, cancellation and crash
recovery become `dispatch_outcome_unknown`; deadline recovery fences a late completion. This prefers a
visible uncertain result over a duplicate external side effect. Adapters own credentials and endpoint
authority, must observe cancellation and must normalize provider exceptions before returning across
the Application port.

The GitHub Issues token is bound only from Worker host configuration, normally the
`IncidentCompass__Tickets__GitHub__Token` environment variable. It is absent from public triage
configuration, config snapshots, tool definitions, prompts, artifacts and report payloads. The
repository is also host-owned; model arguments, incident fields and tenants cannot select another
repository or API authority. The adapter does not log authorization headers, response bodies or
issue bodies. Authentication, rate-limit, timeout and malformed-response failures are reduced to a
closed sanitized code before they reach durable tool outcomes or report limitations. Caller/job
cancellation on read-only search propagates instead of being misreported as a connector timeout.

Ticket create additionally requires exactly one current-attempt durable no-match bound to that same
repository. The check and current binding comparison occur inside the fault-locked proposal
transaction. The model cannot call the create tool or provide repository, owner, authority, token,
marker or category. Every create remains requested until the tenant operator submits exact frozen
hashes. Before the single possible POST, bounded local history and GitHub marker lookup fail closed;
an earlier uncertain marker is read-only and can never authorize another POST. Once the POST starts,
transport or response ambiguity becomes `dispatch_outcome_unknown` and is not retried.

Ticket update is a separate mandatory-approval capability and is limited to one backend-built evidence
comment on exactly one cited `ExistingTicket`. The provider-neutral Application resolver exposes only
report and ticket identity; Infrastructure binds the evidence to the configured repository and
rechecks it inside the fault-locked proposal transaction. Model output, API input and proposal
arguments cannot choose a repository or replace the cited issue. The adapter validates the exact
issue with bounded target and comment-history reads before one possible POST. Missing, foreign,
multiple, malformed or ambiguous evidence and any preflight failure authorize no write. An existing
marker suppresses another comment; uncertainty after POST begins becomes `dispatch_outcome_unknown`
and is never automatically resent.

Telegram routing separates public policy from secret host authority. The snapshotted configuration
contains at most 32 ordered route ids with optional normalized service/environment selectors and a
closed severity subset. First match wins; there is no fanout and no recipient, endpoint, token or
message template in the route. The Worker binds the selected route id to one fixed chat id and bot
token through `IncidentCompass__Telegram__RouteId`, `__ChatId` and `__BotToken`. The adapter accepts
only backend-generated report and route ids, builds the bounded message itself, always targets
`https://api.telegram.org`, follows no redirects and sends no `parse_mode`. Provider bodies, request
paths and tokens are never returned or durably recorded. Once a mutating send starts, exceptions and
cancellation are outcome-unknown and are not retried.
