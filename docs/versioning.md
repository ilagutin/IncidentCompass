# Versioning

This project versions more than releases. Model-backed behavior depends on code, models, embeddings and pricing.

## Project Releases

- Use SemVer.
- Stay in `0.x` until extension points and public contracts stabilize.
- Treat `v0.1.0` as the first public reference release.
- Reserve `v1.0.0` for a future stable public contract surface.

## Public Release Flow

The public repository treats `main` as release-ready history. A public pull request should be ready to become a GitHub Release as soon as it is merged.

- Public changes land through pull requests into protected `main`.
- Merge commits are disabled, so `main` stays linear. Rebase and merge is the strategy for a pull request that carries several commits, which keeps one logical change per public commit; squash merge is for a pull request that is already a single commit. `v0.2.0` landed as one squashed commit and `v0.3.0` landed as its own granular history.
- Release PRs update `VERSION` with SemVer without a leading `v`, for example `0.2.0`.
- Release PRs also update `CHANGELOG.md` and add `docs/release-notes-v<version>.md`, for example `docs/release-notes-v0.2.0.md`.
- After a release PR that changes `VERSION` is merged to `main`, the `publish-release` workflow runs automatically on that `main` push.
- The workflow reads `VERSION`, builds the tag name (`v0.2.0`), verifies the matching release-notes file, reruns the release gate, tags that event SHA and creates the GitHub Release.
- That event SHA is the new `main` tip, which after a rebase merge is the last commit of the pull request rather than the commit that changed `VERSION`. The two are the same only when the `VERSION` change is the final commit.
- Non-release PRs must not change `VERSION`; their merges do not run `publish-release`.
- If the tag already exists at the same `main` commit, the workflow can resume publishing. If the tag exists at another commit, the workflow fails instead of moving history.
- Normal releases do not require pressing `Run workflow`; the `VERSION`-changing merge to `main` is the release trigger.
- `workflow_dispatch` is the recovery path and must run from `main` with a version matching `VERSION`; direct tag pushes do not publish releases.

Do not auto-increment release versions in CI. The version is part of the reviewed release PR so maintainers can choose patch, minor or major intentionally.

## Dependency Lock Files

Every project commits a `packages.lock.json`. `ci`, `security` and `publish-release` all restore with
`dotnet restore IncidentCompass.slnx --locked-mode`, so a dependency that resolves differently from
the committed graph fails the build instead of changing silently.

Regenerate the lock files for the whole solution after any dependency change:

~~~powershell
dotnet restore IncidentCompass.slnx --force-evaluate
~~~

Run it for the solution, not for one project. Package versions are managed centrally in
`Directory.Packages.props`, so a single bump changes the effective graph of every project that
reaches the package through a `PackageReference` or a `ProjectReference`. Commit every
`packages.lock.json` the command changes.

### Dependabot Lock File Sync

Dependabot regenerates only the lock file of the project it edited. Its pull requests therefore
arrive with the downstream projects stale, and `ci` fails on them with two `NU1004` errors: a
`CentralTransitive` requested-version mismatch, and "the project references incidentcompass.api
whose dependencies has changed".

`.github/workflows/dependabot-lockfiles.yml` repairs that without a maintainer. On a Dependabot pull
request it runs the `--force-evaluate` restore above, verifies the result restores in locked mode,
and pushes only the changed `packages.lock.json` files back onto the Dependabot branch. It commits
nothing when the lock files are already correct, so it never creates an empty commit, and its commit
message carries `[dependabot skip]` so Dependabot keeps rebasing the branch instead of stopping
because the branch was modified.

`--locked-mode` is never relaxed. The `ci` re-run on the synced branch, `main` after the merge and
the release gate all still verify the committed lock files.

Security constraints on the workflow, which is the only one in this repository with write access:

- It is a `pull_request` workflow, not `pull_request_target`, so it never runs base-branch logic
  against a writable base-repository context.
- The job runs only when `github.actor`, the pull request author and the head branch are all
  Dependabot's and the head branch lives in this repository.
- Job permissions are `contents: write` and nothing else; the workflow default is `permissions: {}`.
- Checkout uses `persist-credentials: false`, so no token sits in the workspace while
  `dotnet restore` executes MSBuild logic supplied by NuGet packages.
- The commit and push run with `core.hooksPath=/dev/null` and `--no-verify`, so a package that
  planted a git hook cannot run during the push.
- Only `packages.lock.json` paths are staged, and no secret other than the push token is exposed.

#### One-time setup: `DEPENDABOT_LOCKFILE_TOKEN`

GitHub does not create ordinary workflow runs for events caused by `GITHUB_TOKEN`. A `pull_request`
re-run caused by such a push is created in an "approval required" state, so a push made with the
default token fixes the lock files but leaves the pull request waiting for one
"Approve workflows to run" click.

To remove that click, add a **Dependabot** secret (Settings, Secrets and variables, Dependabot; not
Actions, because Actions secrets are not exposed to Dependabot-triggered runs) named
`DEPENDABOT_LOCKFILE_TOKEN`. Store a fine-grained personal access token scoped to this repository
only, with `Contents: Read and write` and no other permission. The workflow then pushes with that
token, the push is an ordinary push, and `ci` re-runs and reports green on its own.

The token is a long-lived credential. Keep it single-repository and single-permission and track its
expiry; a GitHub App installation token minted with `actions/create-github-app-token` is the
alternative when a non-expiring credential is preferred. Without the secret the workflow still works
and warns in the job summary, it just costs that one approval click per pull request.

## API

- Use `/api/v1/...` from the start.
- Avoid breaking documented `v1` response contracts once examples depend on them.

## Database

- Keep schema changes in source control.
- Live ledger/report/memory/action-approval tables and pricing state use explicit raw SQL/init scripts and small Npgsql adapters while the persistence surface is still stabilizing.
- Released migrations are append-only: a shipped script is never renumbered, reordered or given new
  statements. `006-tool-audit.sql` still creates an unused legacy table even though the retired
  standalone application stack no longer has an adapter.
- The migration scripts are frozen as of the 0.4.0 release, not as of first publication. Before 1.0,
  six scripts (`004`, `006`, `007`, `008`, `009`, `010`) had internal tracker identifiers and roadmap
  labels removed from their comments. All six belong to catalog migration version 1, which the ledger
  records under a single checksum, and the checksum covers comment text, so that edit changed that
  one checksum on purpose. It was safe to make exactly once, because there is no deployed database,
  no installation and no upgrade path from a running system: the only databases that existed were
  local demo volumes, recreated by the documented `docker compose down -v` step in
  `docs/quickstart.md`. The freeze holds from that release forward; the startup checksum guard
  described below is what enforces it.
- New applied and failed migration records use one platform-independent SHA-256 checksum. The input is
  each script name plus its decoded SQL encoded as UTF-8 after removing one leading decoded BOM and
  normalizing CRLF or bare CR line endings to LF. Script names, ordering and every other SQL character
  remain checksum-significant.
- The migrator also recognizes the CRLF form of the frozen catalog text for the matching version and
  name, so a ledger written on Windows is not rewritten and its migration is not rerun. This
  compatibility is a closed two-entry list per migration, LF and CRLF of the same frozen text, not
  permission to accept arbitrary alternate hashes.
- Before an upgrade, back up the database and keep the released migration files unchanged. A checksum,
  name or version mismatch outside the closed compatibility set stops startup with a diagnostic. From
  0.4.0 forward the fix is to restore the released files or the migration ledger from a trusted backup,
  not to edit either in place. The one edit described above was taken deliberately before 1.0, when no
  durable database existed to protect; a maintainer who wants to change a shipped script after 0.4.0
  adds a new numbered migration instead, and a repository check
  (`scripts/internal-reference-gate.ps1`) keeps the removed labels from coming back.
- `023-action-approvals-outbox.sql` is catalog migration version 14. Approval contract version 1 is
  stored per action and hashes the complete immutable review tuple with domain separation and
  length-prefixed fields. A future tuple change requires a new contract version and an additive
  migration; released tuple rows and provenance are never rewritten.
- If EF Core is introduced later for broader persistence, use migrations and name them after the use case or schema change.

## Pricing

`incidentcompass.ai_model_pricing` rows carry effective dates, so a cost calculation resolves the
price in force at the call timestamp rather than the current one, and a historical window stays
reproducible after a price change. Rows are operator-maintained database configuration; see
[Cost tracking](cost-tracking.md) for the read model built on them.

Reproducible does not mean frozen. Spend is recomputed from the price table on every read, so
correcting a price in place changes what an already-closed window reports. Catalog migration version
21 (`030-model-price-administration.sql`) is what keeps that honest rather than preventing it: a
write must name its author, the change time is stamped by the database, two intervals cannot cover
one instant for a provider and model, and a price is retired by setting `effective_to_utc` rather
than deleted. `docs/single-host-production.md` gives the procedure and states the effect of each
operation on figures already reported.

## Tool Calls

Tool-call reproducibility on the live governed path comes from the triage ledger, not from a
per-call tool schema version or tool policy version.

- Every `incidentcompass.triage_ledger` row carries a `config_hash` that references an immutable
  `incidentcompass.triage_config_snapshots` row (`infra/postgres/init/008-triage-ledger.sql`,
  `infra/postgres/init/007-intake.sql`). The snapshot holds the serialized configuration and
  instructions in force, and it is written once per hash and never updated. A job stamps its own
  `config_hash` onto every event it appends
  (`src/IncidentCompass.Application/Investigation/Jobs/TriageLedgerAppender.cs`), so a
  `ToolProposed`, `PolicyDecision` or `ToolResult` event stays readable against the exact tool
  registration and rule set that produced it after the current configuration changes.
- Every `incidentcompass.action_approvals` row carries `approval_contract_version`, pinned to 1 by a
  check constraint and held immutable by the lifecycle trigger
  (`infra/postgres/init/023-action-approvals-outbox.sql`). That is the versioned contract for
  backend-owned post-report actions: it fixes the shape of the review tuple whose hash an operator
  approves. Changing that tuple requires a new contract version and an additive migration.
- `incidentcompass.tool_audit_logs` does declare `tool_name`, `schema_version` and `policy_version`,
  but its own header marks the table dormant (`infra/postgres/init/006-tool-audit.sql`) and no code
  under `src/` reads or writes it. Those three columns are reserved for a future governed
  tool-execution audit surface and hold no data today, so they are not the reproducibility
  guarantee for anything shipped.
