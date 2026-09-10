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

Generate a new 32 to 128 character base64url API key in a trusted local secret-handling process and
store only its 64-character SHA-256 digest as `INCIDENTCOMPASS_API_KEY_SHA256`. Keep the raw key in the
client/operator credential store. IncidentCompass receives it only in the
`X-IncidentCompass-Key` request header. The environment file contains the digest, tenant and stable key
id. Model, embedding and GitHub tokens remain Worker/host configuration and must not enter the public
triage configuration, logs or release evidence.

`INCIDENTCOMPASS_SOURCE_ROOT` must be an existing absolute host directory. It is mounted read-only at
`/monitored-source`. The service and release in the environment file must match the host-owned mapping
and the reviewed `CurrentReleases` entry in the triage configuration. This release also requires the
current GitHub owner, repository and token binding. Remediation commands, branch credentials and pull
request bindings are deliberately deferred to later v0.4 work.

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

## Stop and restart

```powershell
docker @compose stop
docker @compose start
```

Do not use `down --volumes` for routine shutdown. The named PostgreSQL volume is the durable database.

## Payload retention

The Worker runs payload retention on a timer of its own. Every 15 minutes it makes one pass: one
bounded run of raw signal payload compaction, then one bounded run of attempt artifact reaping. Both
are on by default in a running Worker, and the API never runs either.

What a pass changes:

- Signals that intake received more than `SignalPayloadRetentionDays` ago (30 by default) have their
  raw `attributes` and `body` emptied, and `payload_compacted_at_utc` set to record that retention
  did it. The signal row, the fault it opened and every field the pipeline derived from the payload
  stay exactly where they were.
- Triage artifacts belonging to an attempt that is no longer their job's current attempt, and older
  than `AttemptArtifactRetentionDays` (7 by default), are deleted. Job-level artifacts, anything a
  report cites, anything an approval or its provenance points at, and the `ProposedAction` and
  `ActionResult` kinds are never candidates.

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
compact no payload and reap no artifact, so an operator reading a Worker log can tell a switched-off
retention from a broken one. Nothing accumulates that cannot be drained later: turning it back on
resumes from whatever backlog built up. `IncidentCompass__RetentionSchedule__IntervalMinutes` changes
the interval and accepts 1 to 1440; a value outside that range fails Worker startup rather than being
clamped, as an out-of-range retention window does.

A failed pass is a warning in the Worker log, not an outage. The two operations are independent, so
one failing still lets the other run, and the failed one is retried on the next pass after a backoff.

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

This process has no automatic failover, HA PostgreSQL, Azure integration, enterprise RBAC, managed
secret store or protection from a hostile co-tenant on the same machine.
