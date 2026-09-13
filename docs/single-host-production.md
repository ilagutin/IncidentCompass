# Single-Host Production Runbook

This runbook describes the bounded single-host deployment this release exercises: one trusted machine,
one trusted operator, one host-owned monitored checkout and one durable PostgreSQL database managed
with Docker Compose. It is a bounded reference deployment. It is not a hostile multi-tenant service,
an HA topology or a disaster-recovery site, and this project does not offer support commitments for it.

## Host prerequisites

- Docker Engine with Docker Compose 2.24.4 or newer. The production overlay uses the Compose
  `!override` tag to replace Development port bindings.
- PowerShell 7 (`pwsh`) for preflight, backup and restore scripts.
- A fixed absolute path to the monitored checkout, owned and reviewed by the operator.
- Enough local capacity for PostgreSQL, the API, the Worker, container images and at least eight
  database dumps during rotation. As an initial planning assumption, reserve 4 CPU cores, 8 GiB RAM
  and 20 GiB plus database and backup growth. Compose does not claim or enforce sizing for a workload.
- Outbound HTTPS from the Worker to `huggingface.co` on its first start with an empty model volume,
  unless the model is installed offline; see "Local embedding model". The in-process embedding model
  adds roughly its file size plus 100 to 200 MB to the Worker's memory and about 123 MB of disk.
- Protected host storage for `.env.production`. This deployment uses a host-owned environment file,
  not a managed secret store. Restrict its filesystem permissions and exclude it from backups that do
  not have equivalent protection.

The API and PostgreSQL ports bind to `127.0.0.1` by default. If the API must be reachable from another
machine, put an authenticated TLS reverse proxy in front of the loopback port and configure the host
firewall before accepting traffic. Do not change the binding to `0.0.0.0` as a substitute for those
controls. PostgreSQL is intended only for local operator access and must remain loopback-bound.

## Prepare configuration

Copy `.env.production.example` to the ignored `.env.production` file and replace every placeholder.
The Development database identity and password, disabled API-key auth, demo model names and keys,
missing provider, source or GitHub bindings, and incomplete enabled Telegram bindings are rejected.

Memory embeddings default to the in-process model: `INCIDENTCOMPASS_EMBEDDINGS_PROVIDER=LocalOnnx`,
`INCIDENTCOMPASS_EMBEDDINGS_PROVIDER_ID=local-embed` and
`INCIDENTCOMPASS_EMBEDDINGS_MODEL=intfloat/multilingual-e5-small`, with no endpoint or key. To embed
through an OpenAI-compatible server instead, set the provider to `OpenAICompatible`, the provider id to
`local-oai`, the model to that server's model id, and `INCIDENTCOMPASS_EMBEDDINGS_BASE_URL`,
`INCIDENTCOMPASS_EMBEDDINGS_PATH` and `INCIDENTCOMPASS_EMBEDDINGS_API_KEY`. Preflight rejects a
provider other than those two, a provider id that does not match the provider, and a demo or
placeholder embedding model name; for `OpenAICompatible` only, it also rejects a missing embedding
endpoint, path or key, an endpoint that is not HTTPS without the loopback override, a malformed path and
the demo key.

Generate a new 32 to 128 character base64url API key in a trusted local secret-handling process and
store only its 64-character SHA-256 digest as `INCIDENTCOMPASS_API_KEY_SHA256`. Keep the raw key in the
client/operator credential store. IncidentCompass receives it only in the
`X-IncidentCompass-Key` request header. The environment file contains the digest, tenant and stable key
id. Model, embedding and GitHub tokens remain Worker/host configuration and must not enter the public
triage configuration, logs or release evidence.

`INCIDENTCOMPASS_SOURCE_ROOT` must be an existing absolute host directory. It is mounted read-only at
`/monitored-source`. The service and release in the environment file must match the host-owned mapping
and the reviewed `CurrentReleases` entry in the triage configuration. This release also requires the
current GitHub owner, repository and token binding. Code publication reuses that same binding and
adds one setting, `INCIDENTCOMPASS_GITHUB_BASE_BRANCH`, which is empty in the sample: leave it empty
unless `branch_push` and `pr_create` are enabled, because empty means code publication is not
configured on this host and every call refuses. Setting it widens what the shared token needs, to
write access on repository contents and pull requests. No test command is run anywhere in this
release, so there is nothing to configure for one.

The remediation diff pass copies that checkout into a disposable workspace, but only when
`IncidentCompass:SourceContext:WorkspaceRoot` names an absolute writable directory. It is unset in
the shipped configuration. When it is set it must not be the monitored root or sit below it, and the
host needs room for a copy of the checkout. See "Remediation diff pass" below for what it does and
what it costs, and `docs/security-model.md`, "Remediation diff boundary", for what it does not
prove: no test is executed, so a produced diff is not evidence that a change builds or passes.

Telegram is disabled by default. If the reviewed triage configuration enables its route, set
`INCIDENTCOMPASS_TELEGRAM_ENABLED=true` and provide the exact route id, chat id and bot token. Otherwise
leave all three binding values empty.

Run preflight from the repository root:

```powershell
pwsh -NoProfile -File scripts/production-preflight.ps1 -EnvironmentFile .env.production
```

Preflight performs secret-free value checks and `docker compose config --quiet`. It activates only
`postgres`, `api` and `worker`. The `tester`, mock overlay and demo profile are not production surfaces.
It rejects conflicting production values inherited from the calling process, so an omitted optional
binding cannot silently come from ambient shell state.

## Start and verify

Use the same file and project name for every command:

```powershell
$compose = @(
  "compose", "-f", "docker-compose.yml", "-f", "compose.production.yml",
  "--env-file", ".env.production"
)
docker @compose up --detach --build postgres api worker
docker @compose ps
docker @compose logs --tail 100 api worker postgres
```

All three services must report healthy. API and Worker images run as the non-root `incidentcompass`
user; the PostgreSQL image runs as its non-root `postgres` user. All production and recovery services
use `json-file` log rotation, 10 MiB and five files by default, and the long-running services use
`unless-stopped` restart behavior. Inspect only bounded log tails. Do not run `docker compose config`
without `--quiet` in shared output because rendered environment values include secrets.

Check the anonymous health endpoint through loopback, then make an authenticated bounded read with the
raw operator key from the client credential store:

```powershell
Invoke-WebRequest http://127.0.0.1:5198/api/v1/health -UseBasicParsing
```

### Database startup ordering

The API and the Worker both reach PostgreSQL while they are still starting: migrations run from a
hosted service and the triage configuration persists its snapshot from a warmup hosted service,
both at host start. In this Compose deployment both services already declare `depends_on: postgres`
with `condition: service_healthy`, so the ordinary `up` is gated on the database health check and
is not what this budget is for. What it covers is a host that starts outside that gate, by hand or
under another supervisor, and the window between the health check passing and the first real
connection being made. Both hosts retry that first connection within a bounded budget, and the
budget is configuration rather than container policy.

Four settings control it. None of them appears in the shipped configuration, so the defaults below
apply as they are. To override one, put it at `IncidentCompass:Postgres:StartupRetry:<name>` in an
appsettings file or pass `IncidentCompass__Postgres__StartupRetry__<name>` as an environment
variable: `MaxAttempts` (10, the total attempts including the first, so 1 disables retrying),
`InitialDelayMilliseconds` (250, the wait before the second attempt, doubling after each failure),
`MaxDelayMilliseconds` (2000, the ceiling for that doubling) and `MaxTotalDurationSeconds` (30, the
expiry). The budget ends at whichever bound is reached first. The expiry is checked between
attempts and does not interrupt an attempt already in flight, so the worst-case wait is the expiry
plus one connection timeout. All four are bounded at both ends and a value outside its range stops
the host at start. The floor is 1 for each of them, so there is no zero attempt count, no zero delay
that would retry as fast as the operating system can refuse the port, and no zero expiry. The
ceilings are 100 attempts, 60000 milliseconds for either delay and 600 seconds of expiry, because an
hours-long silent wait is not the loud failure this budget promises.

One cross-field rule sits on top of those ranges: `MaxDelayMilliseconds` must not be below
`InitialDelayMilliseconds`, because a ceiling under the first wait is a budget that contradicts
itself. It is worth knowing before an override, since both values can be inside their own ranges and
still fail the host at start together - setting `MaxDelayMilliseconds` to 100 while
`InitialDelayMilliseconds` keeps its default of 250 is exactly that case.

Four failures are retried and nothing else: a socket error reaching the endpoint, which is what a
port with nothing listening on it produces; a connection accepted and then dropped mid-handshake,
which is what PostgreSQL does when it takes a connection off its listen backlog before the
postmaster is serving; a timeout reaching the endpoint; and PostgreSQL answering SQLSTATE `57P03`,
the server saying it is up but still starting. Everything else fails immediately, because waiting
cannot change it: a wrong credential, a missing database, an unparsable connection string, and the
errors a running server returns when it is refusing work, such as `too_many_connections`.

Two details of that list are easy to read past. A hostname that does not resolve is not a fifth
category beside the socket error but a carve-out inside it: the socket shape covers every socket
error except `HostNotFound` and `NoData`, so a name that does not resolve is rethrown on the first
attempt rather than burning the whole budget on every host start. And the check walks a failure's
own chain of inner exceptions and does not fan out into an `AggregateException`, so a connection
attempt that tries several hosts and aggregates what each one returned is not retried at all. That
is the fail-safe direction, and it is why this budget is documented for the single-database
deployment this runbook describes.

The budget also covers the first successful connection only. Once one connection has opened, every
later failure surfaces at once, so a database that goes away in steady state stays visible as a
failure, and restarting PostgreSQL under a running API or Worker is not retried at all.

Exhausting the budget fails the host loudly with the normalized persistence error, and the process
exits. The production overlay sets `restart: unless-stopped` on `postgres`, `api` and `worker`, so
the container comes back after that loud failure and tries again from the start; `docker-compose.yml`
on its own sets `restart: on-failure` on the API and the Worker. Either way the restart policy is
now a second line of defence rather than the mechanism that implements startup ordering. This
setting bounds startup ordering only. It is not a guarantee of steady-state database availability.

## Stop and restart

```powershell
docker @compose stop
docker @compose start
```

Do not use `down --volumes` for routine shutdown. The named PostgreSQL volume is the durable database.

## Memory corpus commands

The Worker is the only service composed with an embedding client, so the memory seed pass and the
`memory status` and `memory rebuild` corpus commands run in the worker container, never in the API:

```powershell
docker @compose run --rm worker memory status
docker @compose run --rm worker memory rebuild
```

`memory status` exits 1 when a rebuild is needed, and when the installed local embedding model cannot
serve the configured route, and 0 otherwise. `docs/quickstart.md`, "Changing The Embedding Route",
describes what a rebuild publishes and what it leaves current when it fails.

## Local embedding model

The Worker embeds memory with the in-process model unless the environment file selects
`OpenAICompatible`. `docs/model-gateway.md`, "Local Embedding Model", describes the model, its store and
its identity; this section is what an operator does with them.

**Where it lives.** The `embedding-models` named volume, mounted at `/app/models` in the worker
container only. It holds `manifest.json`, `manifest.previous.json` once an install has replaced a model,
and each file at `artifacts/<sha256>/<file name>`. The Worker image creates `/app/models` owned by its
non-root user, so a fresh volume is writable by the Worker. `scripts/postgres-backup.ps1` does not back
the volume up, because its contents are reproducible from the pinned source; `down --volumes` deletes it
and the next start downloads the model again.

**First start.** A Worker starting on an empty volume downloads the two files, about 123 MB, over HTTPS
from `huggingface.co` and the HTTPS location it redirects to, verifies both digests and writes the
manifest, all before its memory seed pass. Its start waits for that for up to
`IncidentCompass__Embeddings__LocalOnnx__InstallTimeoutSeconds`, 900 seconds by default. Later starts
hash the installed files again and download nothing.

**Offline install.** On a host without that outbound access, place the two files in the volume before
the first start, each at `artifacts/<sha256>/<file name>` under `/app/models`, where `<sha256>` is the
file's lowercase SHA-256 and the name is the last segment of its pinned URL:

- `artifacts/dd476dd0c2514e9b9be83aeb3853fac0763e0bdf4a71645407587d77c48a2d88/model_qint8_avx512_vnni.onnx`
- `artifacts/cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865/sentencepiece.bpe.model`

Download the two files from their pinned URLs on a machine that can, and put them in a host directory,
for example `D:\EmbeddingModelFiles`. Then copy them into the volume from a one-off worker container
that mounts that directory read-only. The container runs as the image's non-root `incidentcompass`
user, so the directories and files it creates are owned by the user the Worker runs as:

```powershell
$model = "/app/models/artifacts/dd476dd0c2514e9b9be83aeb3853fac0763e0bdf4a71645407587d77c48a2d88"
$tokenizer = "/app/models/artifacts/cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865"
docker @compose run --rm --no-deps --entrypoint sh --volume D:\EmbeddingModelFiles:/model-files:ro worker `
  -c "mkdir -p $model $tokenizer && cp /model-files/model_qint8_avx512_vnni.onnx $model/ && cp /model-files/sentencepiece.bpe.model $tokenizer/"
```

Copying with `docker cp` instead writes the files as root. The Worker can still read them, but a later
`memory model install` cannot create its own directories under a root-owned `artifacts` directory, so
prefer the container above.

The Worker verifies files it finds there instead of downloading them, and writes the manifest itself.
The files must be readable by the Worker's user and `/app/models` must stay writable by it. A file whose
digest is wrong is refused and left in place, never replaced, so remove it by hand. Where outbound access
exists only for a maintenance window, `memory model install` can run in that window instead.

**Commands.** Both run in a one-off worker container before any host starts, and both require the host
embedding provider to be `LocalOnnx`:

```powershell
docker @compose run --rm worker memory model status
docker @compose run --rm worker memory model install
```

`memory model status` prints the installed model's id, revision, license, encoded identity and both
digests, the configured memory route, and the model the active corpus was built with. It exits 1 when no
model is installed, when the installed id is not the model the route names, or when the active corpus
was not built under the installed model's encoded identity. Because of that last check it also exits 1
on a host whose corpus has not been seeded yet, and after an install until `memory rebuild` has
re-embedded the corpus.

`memory model install` installs the model the host settings describe beside the installed one: it
verifies or downloads both files, then replaces `manifest.json` and keeps the manifest it replaced as
`manifest.previous.json`. Exactly one previous manifest is kept, so the next install overwrites it, and
no artifact directory is ever deleted. When the configured model is already active it verifies both
files and changes nothing. It is bounded by `InstallTimeoutSeconds` like the start-time pass; a run that
times out prints `embedding_model_install_timed_out` and leaves the active manifest in place. Before it
places files, an install removes temporary `.partial` downloads older than `InstallTimeoutSeconds`, which
only a killed install leaves behind.

**Changing the installed model.** `memory model install` installs the model the Worker's
`IncidentCompass:Embeddings:LocalOnnx` settings describe. Neither compose file passes those settings to
the worker except `ModelDirectory`, so with the shipped files it installs the pinned default, which is
what moves an existing volume to a new release's default. To install another model or revision, add
the settings to the worker in an override file of your own and pass it to every command, including
the long-running Worker:

```yaml
# compose.embedding-model.yml
services:
  worker:
    environment:
      IncidentCompass__Embeddings__LocalOnnx__ModelId: <model id>
      IncidentCompass__Embeddings__LocalOnnx__Revision: <revision>
      IncidentCompass__Embeddings__LocalOnnx__ModelFileUrl: https://<host>/<path>/<model file>
      IncidentCompass__Embeddings__LocalOnnx__ModelFileSha256: <64 lowercase hex>
      IncidentCompass__Embeddings__LocalOnnx__TokenizerFileUrl: https://<host>/<path>/<tokenizer file>
      IncidentCompass__Embeddings__LocalOnnx__TokenizerFileSha256: <64 lowercase hex>
```

```powershell
$compose += @("-f", "compose.embedding-model.yml")
```

`ModelId`, `Revision`, `ModelFileUrl`, `ModelFileSha256`, `TokenizerFileUrl` and `TokenizerFileSha256`
change together, and so does the route model: `INCIDENTCOMPASS_EMBEDDINGS_MODEL` in `.env.production`
must equal the new `ModelId`. A model that differs in more than its file also needs `Dimensions`,
`MaxTokens`, `QueryPrefix`, `PassagePrefix` or `License` set the same way. Both URLs must be absolute
HTTPS URLs ending in a file name, both digests must be lowercase SHA-256 values and must differ, and the
Worker refuses to start on a value that breaks one of those rules. Preflight does not read the override
file.

A running Worker keeps the install state it read when it started, so every procedure that changes the
volume ends by restarting the Worker:

1. `docker @compose run --rm worker memory model install`.
2. Set `INCIDENTCOMPASS_EMBEDDINGS_MODEL` in `.env.production` to the installed model's id if it changed,
   and rerun preflight.
3. `docker @compose run --rm worker memory rebuild`, which re-embeds every reviewed file under the new
   model's encoded identity and publishes one new generation.
4. `docker @compose up --detach --force-recreate worker`.

The restart in step 4 is also what updates the reported state: a `memory rebuild` run as a command does
not update the synchronization status the API reads, so the API keeps reporting the old state until the
restarted Worker's start pass records the new one.

**Rolling back an install.** The previous model's files are still in the volume. Stop the Worker, restore
the previous manifest and start the Worker again:

```powershell
docker @compose stop worker
docker @compose run --rm --entrypoint cp worker /app/models/manifest.previous.json /app/models/manifest.json
docker @compose start worker
```

If no `memory rebuild` ran since the install, the previous corpus generation is still current and matches
the restored model again. If one ran, the current corpus was built with the newer model, and the Worker
reports `memory_embedding_route_changed` until `memory rebuild` re-embeds it with the restored one;
restart the Worker after that rebuild, as above. If the install came with a changed
`INCIDENTCOMPASS_EMBEDDINGS_MODEL`, set it back as well.

**When the model cannot serve the route.** Two states are reported instead of a corpus change. Neither
stops the Worker and neither touches the corpus: the seed pass publishes nothing, the previous generation
stays current, and the Worker logs the state with its model code.

- `memory_embedding_model_mismatch`: a model is installed, but its id is not the model the memory route
  names. It follows a changed `INCIDENTCOMPASS_EMBEDDINGS_MODEL`, or a release whose route names a new
  model while the volume keeps the old one. Install the named model or set the route back to the
  installed id, then restart the Worker.
- `memory_embedding_model_unavailable`: no usable model is installed, because none has been installed yet
  or the start-time install failed or timed out. The Worker log names the cause, for example
  `embedding_model_fetch_failed` or `embedding_model_digest_mismatch`. Fix it, or install offline, then
  restart the Worker.

For both, `RebuildRequired` is false on `GET /api/v1/health/memory-corpus` and `memory status` exits 1. A
rebuild cannot repair either state, because it would embed under a route the installed model cannot
serve, so `memory rebuild` refuses and exits 1. Once the configured model is installed and the Worker
restarted, a corpus built with another model reports `memory_embedding_route_changed`, and a rebuild then
applies.

The places that report these states are not equally current. The Worker log and `memory status` read
the volume directly. The `memory_seed_sync` health check reports degraded with its generic sentence,
"Memory seed synchronization failed; the previous corpus remains active.", while its `lastErrorCode`
names the state. `GET /api/v1/health/memory-corpus` compares only the model id and takes the two model
states from the code the Worker last persisted, so it shows what the Worker recorded at its last
synchronization pass rather than the volume as it is now.

Resolve either state promptly. While it lasts, every triage job that reaches `memory_search` has its
embedding call refused as unavailable and waits and retries as it would during a provider outage.

**Memory and CPU.** The Worker loads the model on its first embedding call and keeps it loaded. Plan for
its memory to grow by roughly the model file's size, about 118 MB, plus 100 to 200 MB; that is a planning
figure, not a measurement recorded in this repository. One embedding call uses one core by default,
`IncidentCompass__Embeddings__LocalOnnx__IntraOpThreads` from 1 to 16, and calls run one at a time. The
int8 file targets processors with AVX-512 VNNI and runs more slowly on processors without it.

## Payload retention

The Worker runs payload retention on a timer of its own. Every 15 minutes it makes one pass: one
bounded run of raw signal payload compaction, then one bounded run of attempt artifact reaping, then
one bounded run of abandoned remediation-workspace reaping. All three are on by default in a running
Worker, and the API never runs any of them.

What a pass changes:

- Signals that intake received more than `SignalPayloadRetentionDays` ago (30 by default) have their
  raw `attributes` and `body` emptied, and `payload_compacted_at_utc` set to record that retention
  did it. The signal row, the fault it opened and every field the pipeline derived from the payload
  stay exactly where they were.
- Triage artifacts belonging to an attempt that is no longer their job's current attempt, and older
  than `AttemptArtifactRetentionDays` (7 by default), are deleted. Job-level artifacts, anything a
  report cites, anything an approval or its provenance points at, and the `ProposedAction` and
  `ActionResult` kinds are never candidates.
- Leftover remediation workspaces under the configured workspace root are deleted, as described in
  "Abandoned remediation workspaces" below. Nothing under the monitored checkout is touched.

Nothing else is touched. No signal, fault, job, report or ledger row is ever removed, and published
reports are refused by the database in any case. The full exclusion list and the reasoning behind the
two windows are in `docs/security-model.md` and `docs/trade-offs.md`.

Each pass deletes or compacts at most `MaxRowsPerRun` rows per operation, 500 by default, so a pass
costs the same whether the database is a week old or three years old. That bound is on what a pass
writes, not on what it reads: the reap has to compare each artifact against its job's current
attempt, which no index can hold, so every pass reads work proportional to the artifact table even
when it deletes nothing. On the 285,000-artifact database measured in
`infra/postgres/init/028-signal-payload-and-artifact-retention.sql` that read is 47 to 148 ms.

**Upgrading an existing database.** The first Worker start after this release finds a backlog: every
signal payload older than the window and every artifact of a superseded attempt is a candidate at
once. There is no catch-up burst. The Worker drains it at 500 rows per operation per pass, which is
48,000 rows a day at the default interval, so a database carrying a few hundred thousand stale rows
is caught up within about a week and disk is reclaimed gradually rather than in one step. Take a
backup before the first start after the upgrade, as the upgrade procedure already requires: once a
payload is emptied the only copy of it is in that dump. To drain faster, raise
`IncidentCompass__Retention__MaxRowsPerRun` for a few days and put it back; that is the setting that
scales with rows. Shortening the interval mostly repeats the table read.

Note that deletion does not by itself return disk to the filesystem. Reclaimed space is reused by
PostgreSQL through autovacuum. Treat a shrinking backlog as the signal that retention is working, not
a shrinking data directory.

**Turning retention off.** Set `IncidentCompass__RetentionSchedule__Enabled` to `false` in the Worker
environment:

```yaml
worker:
  environment:
    IncidentCompass__RetentionSchedule__Enabled: "false"
```

The hosted service still starts and logs, once, that retention is disabled and that this host will
compact no payload, reap no artifact and delete no abandoned workspace, so an operator reading a
Worker log can tell a switched-off retention from a broken one. Nothing accumulates that cannot be drained later: turning it back on
resumes from whatever backlog built up. `IncidentCompass__RetentionSchedule__IntervalMinutes` changes
the interval and accepts 1 to 1440; a value outside that range fails Worker startup rather than being
clamped, as an out-of-range retention window does.

A failed pass is a warning in the Worker log, not an outage. The three operations are independent, so
one failing still lets the others run, and the failed one is retried on the next pass after a backoff.

### Abandoned remediation workspaces

The third operation in the same pass deletes leftover remediation workspaces. A workspace is a
disposable copy of the monitored checkout that the remediation pass creates, reads or patches, and
deletes before it returns. Deleting on every terminal path cannot cover the Worker process being
killed between the copy and the delete, so what a kill leaves behind is a directory under the
workspace root that nothing will ever come back for. This operation is what removes it.

A directory is deleted only when it is under `IncidentCompass:SourceContext:WorkspaceRoot`, carries
the `source-workspace-` prefix, states in its own name an instant more than
`IncidentCompass:SourceWorkspaceRetention:RetentionHours` ago (6 by default), and has not been
written to inside that window either. Anything else is left alone, including a directory whose name
this release cannot read. A run deletes at most
`IncidentCompass:SourceWorkspaceRetention:MaxDirectoriesPerRun` directories, 64 by default, and the
rest waits for the next pass. Both settings are validated at Worker startup: 1 to 168 hours and 1 to
10,000 directories, and a value outside either range fails startup rather than being clamped.

Six hours is deliberately far more than a workspace can live. No workspace survives a single call
into the checkout: the copy is made, identified or patched, and deleted before that call returns, and
nothing holds one open across a model call. The longest a live workspace can exist is one bounded
tree copy plus one bounded patch apply, capped at 20,000 files and 128 MB. A leftover that survives
an extra pass costs disk; a workspace deleted out from under a running pass would turn a working pass
into a filesystem fault, so the window resolves every ambiguity toward keeping.

On a host that sets no workspace root there is nothing to do: no workspace is ever written, and the
operation logs that it is not configured rather than failing or sweeping a directory nobody chose. A
configured root that does not exist yet is reported the same way, because the first pass creates it.

## Remediation diff pass

A remediation pass asks a model for a unified diff that addresses one published report, applies that
diff to a disposable copy of the monitored checkout to prove it applies whole, and records the diff
with the base and result tree identities. It changes no file in the monitored checkout, starts no
process and runs no test. It proposes nothing and dispatches nothing: what it produces is a record
for a human to read.

**What triggers it.** Publishing a report writes a post-report action intent, and the Worker's
post-report evaluation loop runs the pass from that intent. That loop already owns the fenced claim,
the attempt cap, the dead-lettering and the lease renewal a multi-minute model call needs. Nothing
runs a pass on an API request thread, and no pass runs twice for one report.

**What it costs.** One model call per report on the route the job's own configuration snapshot names
for the orchestrator, plus up to `Orchestrator.Budget.MaxReprompts` correction turns if the answer is
not a parseable diff, plus one copy of the checkout per call into the workspace. The tokens are
charged to the same attempt budget that produced the report, so one incident stays one number; a pass
whose attempt has no budget left is refused before the call and dead-lettered. Every call is recorded
as a `ModelCall` ledger row with call kind `remediation`, so `GET /api/v1/observability/model-costs`
separates the cost of preparing a fix from the cost of producing the report.

**Turning it on.** The pass is off in the shipped configuration and is switched exactly like every
other external action, in the reviewed triage configuration rather than in host environment
variables:

```json
"Tools": {
  "remediation_diff": {
    "Kind": "external_action",
    "Category": "code_write",
    "LogicalTargetId": "source:configured-workspace",
    "Mode": "live"
  }
},
"Actions": { "AllowedTools": ["remediation_diff"], "DefaultMode": "live" }
```

All four have to be true: declared with that exact category and logical target, listed in
`AllowedTools`, its own `Mode` not `disabled`, and `Actions.DefaultMode` not `disabled`. Setting any
one of them back to the shipped value switches the pass off. The shipped file declares the tool with
`"Mode": "disabled"` and an empty `AllowedTools`, so turning it on is a deliberate edit to two
places.

The switch is read when the report is published and again when the intent is evaluated, so turning it
off stops passes that were enqueued and not yet run before they spend anything. Because the triage
configuration is snapshotted per job, turning it on applies to reports published under the new
snapshot, not to reports already published under the old one.

**A host that never configures a workspace root.** Nothing fails to start, and nothing is silently
skipped. If the switch is on but `IncidentCompass:SourceContext:WorkspaceRoot` is unset, or the
faulting service and its `CurrentReleases` entry match no configured source root, the pass refuses
before it calls a model, spends nothing, and completes the intent carrying
`remediation_not_configured`. The same is true when the report cites no source evidence
(`remediation_source_evidence_missing`) or the snapshot names no current release for the service
(`remediation_release_unavailable`). Each of those is a settled outcome on the intent rather than a
retry, and each is visible in the Worker log and on the intent row.

**Approving a prepared fix.** A recorded diff is a statement, not an action. Turning one into
something a person may act on is a second capability, `remediation_apply`, switched on the same four
ways in the same file:

```json
"Tools": {
  "remediation_apply": {
    "Kind": "external_action",
    "Category": "code_write",
    "LogicalTargetId": "source:configured-workspace",
    "Mode": "live"
  }
},
"Actions": { "AllowedTools": ["remediation_diff", "remediation_apply"], "DefaultMode": "live" }
```

Both entries are declared and disabled in the shipped file, and they are independent: preparing
diffs for review while allowing none of them to be acted on is a perfectly reasonable state and is
what you get by turning on only the first.

With both on, a pass that produces a diff also creates one `code_write` approval per report, in state
`requested`. It never auto-approves: `code_write` is not an auto-approvable category, so
`Actions.RequireApprovalForAll` makes no difference here. The approval is visible through
`GET /api/v1/action-approvals`, and approving it means echoing both its payload digest and its
approval digest back, which is what binds the decision to the exact bytes that were reviewed.

**What a reviewer sees, and what approving means.** The review summary begins with `UNTESTED CHANGE`,
and the frozen payload says the same thing three ways: `"testOutcome": "not_executed"`,
`"testCommandId": null`, and a `testStatement` sentence ending "Approving it approves an untested
change." That is literal. Nothing in this release starts a process or runs a test command, so no diff
carries any evidence that it compiles, passes anything or is correct. The payload also carries the
exact diff, the base tree identity it applies to, the tree it produced, the service and release, and
a digest over the cited source excerpts it was derived from.

Approving authorizes exactly one thing: re-applying those bytes to a fresh disposable copy of the
approved base and recording the resulting tree identity. It does not push a branch, open a pull
request or merge anything, and the recorded result says so with `"landed": false` beside it.

**When the checkout moved.** The base is checked twice. Before the proposal, a moved checkout refuses
with `remediation_base_stale` and no approval is created. After the approval, the dispatch fails with
`remediation_base_mismatch` and the approval ends in `failed` with that code on it. There is no
re-approval of a failed action, and that is deliberate: the source excerpts the diff was derived from
came from the tree that moved, so the honest recovery is a fresh investigation of the recurrence, not
a fresh diff against the old evidence.

**Other refusals you may see on the intent.** `remediation_diff_missing` (nothing was recorded for
that report), `remediation_diff_ambiguous` (the report has more than one recorded diff, so durable
state names no single change), `remediation_diff_foreign` (the recorded diff names a different job,
attempt, service or release than the report's own) and `remediation_diff_unsupported` (the row does
not carry the one shape this release can freeze). All four create nothing.

**Repointing a monitored root.** The adapter's binding fingerprint hashes the workspace root together
with the resolved monitored roots. Changing either while an approval is outstanding fails its
dispatch with `adapter_binding_changed` rather than applying an approved diff to a directory nobody
approved. Expect to re-run triage after such a move.

## Model prices

`incidentcompass.ai_model_pricing` is the only table an operator is expected to write by hand. There
is no price API and no configuration key that writes one: prices are host-global while every API
identity is tenant-scoped, so a price endpoint would need an admin identity this system does not
have. `docs/cost-tracking.md` explains why the table is shaped the way it is; this section is the
procedure.

Two things are true of every edit here and decide the whole procedure:

- The hourly cost rollup reads this table on every request and recomputes spend from ledger rows. An
  edit therefore takes effect with no restart, and it changes the answer given for windows that
  already closed.
- There is no stored spend figure to contradict. The only thing an edit can contradict is a figure
  someone already read and acted on, which is why every statement below names who is making the
  change.

Connect with `psql` inside the running database container, using the `POSTGRES_USER` and
`POSTGRES_DB` values from `.env.production`:

```powershell
docker @compose exec postgres psql --username <postgres-user> --dbname <postgres-db>
```

**Add a price.** `administered_by` is required and is free text: put something that identifies you
in the operator's own terms. A write without it is refused.

```sql
INSERT INTO incidentcompass.ai_model_pricing (
    id, provider, model, currency,
    input_token_price_per_million, output_token_price_per_million,
    effective_from_utc, effective_to_utc, administered_by, administration_note)
VALUES (
    gen_random_uuid(), 'openai-main', 'gpt-4.1-mini', 'USD',
    0.40, 1.60,
    '2026-09-01T00:00:00Z', NULL, 'ops:alice', 'Published list price, September rate card.');
```

`provider` is matched against the call's **configured provider ID** - the entry in the triage
configuration's `Providers` table - not the adapter name, and the match is case-sensitive for both
provider and model. A price that never matches anything is silently harmless: calls simply read as
unpriced.

Effect on figures already reported: calls in hours from `effective_from_utc` onwards start being
priced, so a window that previously reported unpriced calls now reports spend for them. Hours before
that instant are untouched.

**Change a price going forward.** Close the current interval and open a new one. Do this rather than
editing the price in place: it keeps each row's own author, and it leaves history alone.

```sql
BEGIN;
UPDATE incidentcompass.ai_model_pricing
SET effective_to_utc = '2026-10-01T00:00:00Z', administered_by = 'ops:alice'
WHERE provider = 'openai-main' AND model = 'gpt-4.1-mini' AND effective_to_utc IS NULL;

INSERT INTO incidentcompass.ai_model_pricing (
    id, provider, model, currency,
    input_token_price_per_million, output_token_price_per_million,
    effective_from_utc, effective_to_utc, administered_by, administration_note)
VALUES (
    gen_random_uuid(), 'openai-main', 'gpt-4.1-mini', 'USD',
    0.35, 1.40,
    '2026-10-01T00:00:00Z', NULL, 'ops:alice', 'October rate card.');
COMMIT;
```

Intervals are half-open: the old one covers up to but not including the boundary instant and the new
one starts at it, so the two do not overlap. The database refuses two intervals covering one instant
for the same provider and model, with `ex_ai_model_pricing_no_overlap`. If that happens, one of the
two dates is wrong - fix the date rather than deleting a row.

Effect on figures already reported: none. Every hour before the boundary keeps the price it was
reported with.

**Correct a price that was always wrong.** This is the one operation that rewrites history, so it is
the one that most wants a note.

```sql
UPDATE incidentcompass.ai_model_pricing
SET output_token_price_per_million = 1.20,
    administered_by = 'ops:alice',
    administration_note = 'Transcribed from the wrong column of the rate card on 2026-09-01.'
WHERE provider = 'openai-main' AND model = 'gpt-4.1-mini'
  AND effective_from_utc = '2026-09-01T00:00:00Z';
```

Effect on figures already reported: every hour this interval covers is recomputed at the new rate the
next time anyone reads the rollup. A spend figure someone recorded before the correction will not
match one read after it. Tell whoever holds the earlier figure; the database records only who made
the change and when, not who was told.

**Retire a price.** Set the end of its interval. Do not delete the row - the database refuses
`DELETE` on this table, because a deleted price silently turns priced hours into unpriced ones and
leaves nothing behind to explain the drop.

```sql
UPDATE incidentcompass.ai_model_pricing
SET effective_to_utc = '2026-11-01T00:00:00Z',
    administered_by = 'ops:alice',
    administration_note = 'Model withdrawn by the provider.'
WHERE provider = 'openai-main' AND model = 'gpt-4.1-mini' AND effective_to_utc IS NULL;
```

Effect on figures already reported: none before the end instant. From it onwards, calls to that
model read as unpriced and appear in `unpricedCallCount` rather than in spend.

**Check what is in the table**, including who last touched each row:

```sql
SELECT provider, model, currency,
       input_token_price_per_million, output_token_price_per_million,
       effective_from_utc, effective_to_utc,
       administered_by, administered_at_utc, administration_note
FROM incidentcompass.ai_model_pricing
ORDER BY provider, model, effective_from_utc;
```

`administered_at_utc` is stamped by the database and cannot be set by the statement.
`administered_by` and `administration_note` record only the most recent change to a row, which is the
other reason to prefer closing an interval over editing one.

**Upgrading a database that already holds overlapping prices.** The migration that adds these rails
refuses to apply and names the conflicting provider/model pairs. It will not choose an interval to
close on the operator's behalf. Close or correct one interval of each named pair by hand and rerun
the upgrade; those rows were already ambiguous and therefore already unpriced, so closing them
changes no figure the rollup ever reported.

Rows written before the upgrade keep no author, which is honest: nothing recorded one. The prices
seeded with the schema are the exception and name the schema.

## Backup

Create and protect an explicit absolute host directory, then run:

```powershell
pwsh -NoProfile -File scripts/postgres-backup.ps1 `
  -EnvironmentFile .env.production `
  -BackupDirectory D:\IncidentCompassBackups
```

The default timeout is 30 minutes. A successful backup is a `pg_dump -Fc` custom dump plus a
secret-free JSON manifest containing its byte length and SHA-256. The script retains the newest seven
verified successful pairs. Rotation considers only the IncidentCompass filename grammar, ownership
marker, matching dump filename and matching hash. Unrelated files, partial dumps, orphan dumps,
orphan manifests, malformed manifests and hash-mismatched pairs are never rotation targets.

Copy completed pairs to separately protected storage according to the operator's recovery policy.
This single-host runbook does not claim an automatic second site.

## Upgrade

1. Record the current application revision and `docker compose ps` result.
2. Create a successful backup pair and copy it away from the working checkout.
3. Review the new release notes, production overlay and environment sample for required settings.
4. Check out the reviewed release, rerun production preflight and rebuild the API and Worker images.
5. Run `docker @compose up --detach --build postgres api worker`.
6. Require healthy services and inspect bounded log tails. Normal API/Worker startup runs the canonical
   migration catalog under the PostgreSQL migration lock.
7. Perform authenticated report, ledger and approval reads before returning the service to users.

An upgrade does not change the installed embedding model, even when the release ships a new default:
the Worker keeps the manifest in the `embedding-models` volume. "Local embedding model" describes how to
move to a new model, and what a release whose route names a different model id reports until you do.

Never edit released SQL or migration ledger rows to force an upgrade.

## Restore and recovery smoke

Restore never targets the live project or its volume. Choose a new lowercase Compose project name and
unused loopback API/PostgreSQL ports. The exact project name must be repeated in `-ConfirmTarget`:

```powershell
pwsh -NoProfile -File scripts/postgres-restore.ps1 `
  -EnvironmentFile .env.production `
  -BackupFile D:\IncidentCompassBackups\incidentcompass-backup-<id>.dump `
  -TargetProject incidentcompass-recovery-20260903 `
  -ConfirmTarget incidentcompass-recovery-20260903 `
  -TargetApiPort 15198 `
  -TargetPostgresPort 15432 `
  -WhatIf
```

Remove `-WhatIf` only after reviewing the target. The command rejects the current project, any target
containers or network, an existing named volume, colliding ports, a non-empty initialized target and a
missing or hash-mismatched manifest. It first initializes blank PostgreSQL storage through the
`recovery` profile without application init scripts, restores the custom dump, removes only the helper
container, and then starts only the normal PostgreSQL and API services on that volume. API startup runs
the canonical migration hosted service, and API health remains unavailable until migration readiness
is established. The recovery Worker is deliberately not created during restore.

Success is emitted only after normal migrations, PostgreSQL/API health checks and a bounded count-only
readback of reports, evidence, ledger, approvals, compact external-action projections, memory items and
chunks, and migration history. Failed restore, migration, health or readback leaves the target project
and volume intact for inspection. It never mutates or removes the live source project.

Before starting a Worker, inspect restored pending/retry triage jobs, approved but undispatched actions
and pending post-report intents. Starting it can resume model calls and external GitHub or Telegram
writes from those durable queues. Only after the operator promotes this recovery project to the
authoritative deployment, confirms its outbound bindings and accepts that replay boundary should the
Worker be started explicitly:

```powershell
docker compose -f docker-compose.yml -f compose.production.yml `
  --env-file .env.production --project-name incidentcompass-recovery-20260903 `
  up --detach worker
```

Require Worker health after that explicit promotion. Restore/readback success by itself is not Worker
promotion and cannot dispatch restored work.

The complete smoke creates a new backup and restores it with the recovery Worker still stopped:

```powershell
pwsh -NoProfile -File scripts/production-recovery-smoke.ps1 `
  -EnvironmentFile .env.production `
  -BackupDirectory D:\IncidentCompassBackups `
  -TargetProject incidentcompass-recovery-20260903 `
  -ConfirmTarget incidentcompass-recovery-20260903 `
  -TargetApiPort 15198 `
  -TargetPostgresPort 15432
```

After inspection, cleanup is an explicit operator action against the exact recovery project. Never add
`--volumes` until the recovery volume is no longer needed:

```powershell
docker compose -f docker-compose.yml -f compose.production.yml `
  --env-file .env.production --project-name incidentcompass-recovery-20260903 down
```

## Rollback

Application rollback is a reviewed checkout/image change against a schema-compatible release. Database
rollback is never an in-place ledger edit. Keep the failed upgrade project for evidence, restore the
pre-upgrade backup into another fresh project/volume, validate its health and bounded readback, then
move the reverse proxy or local client to the reviewed recovery API port. Inspect durable pending work
and explicitly start the recovery Worker only after deciding that project is authoritative. Preserve
both database volumes until the operator has decided which state is authoritative.

The embedding model volume is rolled back on its own, not with the application or the database. A new
recovery project starts with an empty `embedding-models` volume, so its Worker downloads or needs an
offline install of the model on first start. An installed model is rolled back as described in "Local
embedding model".

This process has no automatic failover, HA PostgreSQL, Azure integration, enterprise RBAC, managed
secret store or protection from a hostile co-tenant on the same machine.
