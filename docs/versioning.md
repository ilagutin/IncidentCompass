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
- Squash merge is the expected public merge strategy: one merged PR becomes one public release commit.
- Release PRs update `VERSION` with SemVer without a leading `v`, for example `0.2.0`.
- Release PRs also update `CHANGELOG.md` and add `docs/release-notes-v<version>.md`, for example `docs/release-notes-v0.2.0.md`.
- After a release PR that changes `VERSION` is merged to `main`, the `publish-release` workflow runs automatically on that `main` push.
- The workflow reads `VERSION`, builds the tag name (`v0.2.0`), verifies the matching release-notes file, reruns the release gate, tags that event SHA and creates the GitHub Release.
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
- Released migrations are append-only. `006-tool-audit.sql` remains byte-identical and creates an unused legacy table even though the retired standalone application stack no longer has an adapter.
- New applied and failed migration records use one platform-independent SHA-256 checksum. The input is
  each script name plus its decoded SQL encoded as UTF-8 after removing one leading decoded BOM and
  normalizing CRLF or bare CR line endings to LF. Script names, ordering and every other SQL character
  remain checksum-significant.
- During a v0.3 database upgrade, the migrator also recognizes the exact historical LF and CRLF
  checksums for the matching released catalog version and name. An applied legacy row is accepted as-is:
  it is not rewritten and its migration is not rerun. This compatibility is a closed list for released
  migrations, not permission to accept arbitrary alternate hashes.
- Before an upgrade, back up the database and keep the released migration files unchanged. A checksum,
  name or version mismatch outside the closed compatibility set stops startup with a diagnostic. Restore
  the released files or migration ledger from a trusted backup instead of editing SQL or durable rows in
  place.
- `023-action-approvals-outbox.sql` is catalog migration version 14. Approval contract version 1 is
  stored per action and hashes the complete immutable review tuple with domain separation and
  length-prefixed fields. A future tuple change requires a new contract version and an additive
  migration; released tuple rows and provenance are never rewritten.
- If EF Core is introduced later for broader persistence, use migrations and name them after the use case or schema change.

## Pricing

Pricing records include effective dates so historical cost calculations remain reproducible.

## Tool Calls

- tool name;
- tool schema version;
- tool policy version.

These fields keep proposed, approved, rejected and executed tool calls
reproducible after a tool schema or backend policy changes.
