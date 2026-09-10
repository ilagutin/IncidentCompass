# Contributing

IncidentCompass is reference-quality software: it is published to be read as closely as it is run.
A change is judged on whether the repository still explains itself afterwards, which is why the
verification below is not optional and why documentation moves with behavior.

`AGENTS.md` is the full operating contract, including the architecture boundaries and the safety
rules. This file is the short version a first-time visitor needs.

## Before opening a pull request

Run the same checks the pipeline runs, smallest first:

```powershell
dotnet restore IncidentCompass.slnx
dotnet build IncidentCompass.slnx
dotnet test --solution IncidentCompass.slnx
dotnet format IncidentCompass.slnx --verify-no-changes --verbosity minimal
powershell -ExecutionPolicy Bypass -File scripts\package-vulnerability-gate.ps1
powershell -ExecutionPolicy Bypass -File scripts\code-organization-gate.ps1
```

Persistence and schema changes need the Docker-backed integration tests as well:

```powershell
$env:INCIDENTCOMPASS_REQUIRE_DOCKER_TESTS = "true"
dotnet test --project tests\IncidentCompass.IntegrationTests\IncidentCompass.IntegrationTests.csproj
```

If you skip those locally, say so in the pull request so the residual risk is visible rather than
assumed away.

Package versions are managed centrally. After changing one in `Directory.Packages.props`, regenerate
every lock file and commit what changes; CI restores in locked mode, so a stale lock file fails the
build. `docs/versioning.md` has the exact command under "Dependency Lock Files".

## What the pipeline checks

`ci / build` is the single required status check. It runs nothing itself: it passes only when every
other job in the `ci` workflow passed, so one required check covers the whole workflow. The jobs it
aggregates carry descriptive names in the checks list.

## Pull requests

- `main` is protected. Every change lands through a pull request.
- Keep commits atomic and green: one logical change each, each passing the gate on its own.
- Stage intended files by explicit path.
- If a change affects setup, security posture, provider behavior, request or response shape, or
  persistence, update the matching file under `docs/` and the README in the same pull request.
- Do not change `VERSION` in a pull request that is not a release.

## Releases

A release is a pull request that updates `VERSION` with SemVer and no leading `v`, updates
`CHANGELOG.md`, and adds the matching `docs/release-notes-v<version>.md`. Merging it to `main` is
what publishes: the `publish-release` workflow reads `VERSION`, validates the release-notes file,
reruns the build, format, code-organization, vulnerability and test gates, tags the current `main`
and publishes the GitHub release. Manual dispatch and pushed tags are recovery paths, not the normal
route.

## Reporting a vulnerability

See `SECURITY.md`. Please do not open a public issue for a security problem.
