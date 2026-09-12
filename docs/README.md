# Documentation

Three ways in, depending on what you came for. Each tier stands alone, and the order inside a tier
is the order to read it.

## Run it

You want the system running on your machine and something to look at.

1. [Quickstart](quickstart.md) is the single runnable path: prerequisites, the one-command demo,
   how long a first run takes, local configuration, manual runs and provider settings.
2. [Local demo walkthrough](local-demo.md) shows a real demo table, names each scenario, and says
   what the run proves and what it does not.
3. [Integration configuration](integrations.md) turns on the optional source lookup, GitHub,
   Telegram and API-key settings. Each is disabled until a host configures it explicitly.
4. [Single-host production runbook](single-host-production.md) is the separate loopback-only
   deployment path for one trusted machine, with preflight, backup and recovery steps.

## Understand it

You want to know how a signal becomes a grounded report, and where the boundaries are.

1. [Architecture](architecture.md) is the map: projects, intake, the Worker claim loop, memory,
   read-only tools, report lifecycle and the post-report action approval state machine.
2. [Security model](security-model.md) is the governance story: what the model may do, what the
   backend decides for itself, and what fails closed.
3. [Model gateway](model-gateway.md) covers the provider abstraction, routes, bounded failures and
   how usage is accounted.
4. [Observability](observability.md) and [Cost tracking](cost-tracking.md) cover the durable ledger,
   what is deliberately never logged, and the hourly cost rollup built on recorded usage.

## Judge it

You are deciding whether the engineering is any good.

1. [Trade-offs](trade-offs.md) is the most direct answer: every intentional compromise with its
   cost, including the unflattering ones.
2. [Measured evaluation run](evaluation-evidence.md) is the numbers: one pinned real-model run with
   its per-attempt record committed, the misses shown attempt by attempt, and an explicit list of
   what the metrics do not establish.
3. [Code organization](code-organization.md) states the maintainability rules the code is held to,
   and a repository gate enforces part of it.
4. [Versioning and release flow](versioning.md) covers SemVer, the API and database contracts,
   dependency lock files and the migration checksum guard.
5. [Contributing](../CONTRIBUTING.md) and the [Security policy](../SECURITY.md) describe how a
   change lands and how to report a vulnerability.

## Reference

- [Project README](../README.md) for scope, the investigation diagram and the stated limits.
- [Changelog](../CHANGELOG.md).
- Release notes: [0.4.0](release-notes-v0.4.0.md), [0.3.0](release-notes-v0.3.0.md),
  [0.2.0](release-notes-v0.2.0.md), [0.1.1](release-notes-v0.1.1.md) and
  [0.1.0](release-notes-v0.1.0.md).
