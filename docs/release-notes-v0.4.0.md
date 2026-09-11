# IncidentCompass 0.4.0 - a governed remediation chain, real providers, and a measured run

IncidentCompass 0.4.0 is the first release in which the product prepares a change rather than only
describing one. A published report can now become a diff, an approved code write, a branch, a pull
request and a comment back on the ticket that started it. Every one of those external writes is a
separate approval a person grants, nothing merges, and the whole chain ships disabled.

It is also the first release with numbers behind it. An opt-in evaluation harness runs a frozen
corpus against a real local model and the per-attempt record of one run is committed to this
repository, so the claims below can be checked instead of believed.

## The supported envelope

Earlier releases said only that this is reference-quality software for local review. That is no
longer the most useful thing to say, because one deployment shape is now exercised and documented.

**One trusted machine, one trusted operator, one host-owned monitored checkout, one configured
repository and one PostgreSQL database under Docker Compose, with the API and PostgreSQL bound to
loopback.** [Single-host production runbook](single-host-production.md) is that envelope: preflight
that refuses demo credentials, bounded backup, a real fresh-volume restore, recovery smoke and
rollback. If the API has to be reachable from another machine, an authenticated TLS reverse proxy and
a host firewall go in front of the loopback port; changing the binding is not a substitute for them.

Outside that envelope, and not provided: high availability or failover, multi-team role-based access,
enterprise identity, a managed secret store (the host owns a protected environment file), more than
one source repository or an arbitrary one, a UI, general cross-fault incident correlation, and
exactly-once external delivery. Action dispatch is at-most-once backend invocation; an uncertain
outcome stays durable and is left for operator review rather than resent. No process is started
anywhere in the product, so nothing here runs a test, a build or a deployment.

And the standing limit that no amount of engineering below removes: a grounded report is not a
correct report. Evidence checks prove provenance, not truth.

## What changed

### A remediation chain, disabled by default

Five governed capabilities, each gated on the one before it:

- `remediation_diff` prepares a change and writes nothing outside the system. It names the base tree
  before the model is asked, states it in the request, and hands it back to the apply, because an
  insert-only hunk quotes no base line and nothing in a diff binds it to the tree it was written
  against. The answer is parsed strictly and every refusal has its own code: a lenient parser is how
  "we do not support that" quietly becomes "we did something else".
- `remediation_apply` freezes one recorded diff into a `code_write` approval. `code_write` is not an
  auto-approvable category, and configuration can only tighten that floor. Approving authorizes
  exactly one thing: re-applying those bytes to a fresh disposable copy of the approved base and
  recording the resulting tree identity. The recorded result says `"landed": false` beside it.
- `branch_push` publishes an approved, executed code write as one new branch at one new commit.
- `pr_create` opens one pull request from that branch into the configured base branch.
- `ticket_backlink` adds one comment to the issue the report cited, naming the pull request.

Approval is done over the approval API by echoing back both the payload digest and the approval
digest the read returned, which is what binds a decision to the exact bytes a person reviewed. A
wrong digest is a conflict, the row stays requested, and nothing reaches the provider.

**Nothing can merge.** The pull-request port has four methods and none of them is a merge; its
request type has four fields and none could express one; the adapter sends only reads and two creates
to a closed set of paths with no GraphQL client anywhere; and the audit projection admits a single
transition with no merged state to write. There is no force update, no reference delete and no
repository-settings call either.

No model turn can name any of these tools: only immediate read tools are ever offered to a model.
Everything that decides where a change lands is read from what the backend already wrote. The service
comes from intake, the release from the job's own configuration snapshot, the host maps that pair to
a directory, and the branch name is derived from the report. No contract on this path accepts a path,
a root, a branch, a remote or a credential.

In `config/incidentcompass.config.json` all five ship with `"Mode": "disabled"` and
`Actions.AllowedTools` is empty, so enabling any of them is a deliberate edit in two places. See
[Security model](security-model.md) for each boundary and [Architecture](architecture.md) for the
path.

### No test command is executed

This is the part most worth reading twice. **A prepared diff is validated, not verified.** It is
parsed, checked against policy, and applied to a disposable copy of the approved base. Nothing runs
it. A diff carries no evidence that it compiles, passes anything, or is correct.

That is enforced rather than documented. The diff row carries `test_outcome = 'not_executed'` and a
null `test_command_id` under database CHECK constraints, so a release that actually runs a test has
to relax that constraint in its own migration and cannot make the claim quietly. A row saying
anything else is refused before a proposal is built, and so is a payload whose test fields were
edited. The frozen approval payload states it three times inside the hashed bytes, ending in a
sentence that says approving it approves an untested change, and the review summary a person sees
begins with `UNTESTED CHANGE`.

This was a deliberate decision, not an omission waiting to be filled. A fixed test command would
execute model-written code inside a process that holds every credential the deployment has: the
model provider key, the repository token, the database password. The honest version of the feature is
an untested diff that says so loudly. [Trade-offs](trade-offs.md) records the reasoning in full.

### The pushed tree is proved over an intersection, not over everything

The tree that goes out is the remote base branch head's tree with only the patched files overlaid. It
is emphatically not the local copy: admission has no notion of what a repository tracks, so on a real
checkout that copy holds build output and any untracked environment file.

What binds the approved base to the remote commit is arithmetic rather than assumption. Git names a
blob by hashing its length and bytes, so the product computes those ids locally and compares them to
the remote tree path by path. A path whose content differs refuses. A path the remote has and the
local copy does not also refuses, because the commit would otherwise silently resurrect a deleted
file. **Paths only the local copy holds are enumerated and excluded by name**, and the proof, the
counts and every excluded path are hashed into the approval and re-proved at dispatch against the
pinned commit rather than against the branch head.

So byte-equivalence holds over the proved tracked intersection, with a stated boundary. It does not
hold over everything, and the documentation says so rather than implying more.

### Providers became real

The configuration published a table of providers, each with an endpoint and a credential reference,
and nothing read either one. A single host-level client answered every route against one address with
one key. An operator could add a second provider, watch it validate, and never learn that every call
still went to the first.

A provider entry now decides which endpoint answers a route and which credential it carries. The line
is drawn at the provider count rather than at a per-entry default: **one declared provider keeps using
the host section exactly as before**, so the shipped single-provider `Providers` block needs no edit.
Two or more get no default at all, and each must supply its own endpoint and credential reference or
the host refuses to start, because the failure a silent default would produce there is one provider's
key arriving at another provider's endpoint. A credential reference names an environment variable and never a value,
since the configuration loader expands placeholders into the document it hashes and stores as a job's
pinned snapshot. Embedding routes moved with the chat path.

On top of that, a chat route may declare a `FallbackRouteId` and **a failed call is retried there
once**. Only `Unavailable` and `GenerationTimeout` fail over, because those are the two kinds a
different endpoint can plausibly answer. Both calls share the primary's cancellation, so no
configured deadline doubles; both are charged, and the failed call's accounting is made durable
before the second may spend anything; a fallback does not itself fail over; and a fallback answering
does not clear provider backpressure. A report published from an attempt that used one carries a
backend-owned sentence saying it ran degraded. No shipped route declares a fallback.

[Model gateway](model-gateway.md) documents the provider table, the fail-over rules and the shipped
profile.

### Retention runs

A long-running installation accumulated the raw body of every signal it ever accepted and the bulky
tool payloads of every attempt superseded by a later one. Both are now bounded operations, and the
Worker drives them.

One pass every fifteen minutes by default, one bounded run per tick rather than draining, so the cost
is the same on a day-old database as on a three-year-old one. Default windows are thirty days for
signal payloads and seven for attempt artifacts. Compaction empties two columns and nothing else; a
signal row is never deleted, reports refuse updates and deletes outright, and the ledger is the audit
trail the whole exercise exists to keep. The reap excludes job-level rows, two artifact kinds that
are the durable record of a real external effect, and three reference holders. Retention can be
turned off, and a host with it off says so once at start rather than looking broken.

A timeline can now state the consequence. Every ledger event carries a `payloadState` of `Retained`,
`Reaped`, `NotReapable` or `None`, resolved at read time as a scalar so no event is dropped by the
resolution itself. Before this, a reference whose payload had been reaped rendered as exactly the
same string as one that was still there. [Observability](observability.md) explains what backs each
value, including why `Reaped` is a claim about the writers rather than a database constraint.

### Cost accounting had never produced a figure

The cost record named the adapter, not the configured provider. That was harmless while one
host-level client answered everything, and stopped being harmless the moment a host could declare two
providers with different endpoints, credentials and prices.

The consequence was larger than a key collision. The pricing table is keyed on the provider name, and
the shipped price rows were seeded under a name no real call ever wrote, so **every call against a
real provider was already being counted as unpriced**. Nothing was wrong in the totals, because the
accounting refuses to invent a price it cannot find. The feature had simply never once produced a
figure.

The two facts are now recorded separately rather than one being redefined: the adapter says which
client spoke the protocol, the configured provider says who pays. A row written before this release
cannot be priced and stays unpriced rather than being attributed by guess. Spend is built only from
provider-reported token counts, and the rollup reports how many calls and tokens were estimated so a
low figure is an answerable question. Prices remain hand-edited rows, now requiring a named author, a
database-stamped change time, no overlapping intervals and no deletes.
[Cost tracking](cost-tracking.md) has the procedure.

### What the model is shown, and what the report admits

- Worker tool results are redacted before they are stored and before the model sees them. A tool
  result comes from outside the trust boundary, so a source file with a hardcoded credential or a
  ticket comment carrying a token was previously persisted verbatim into a column named
  `redacted_payload` and rendered into the next prompt from the stored row. All four surfaces now
  pass through one factory that redacts, canonicalizes and hashes. A tool cannot express a persisted
  artifact at all any more; it returns a draft.
- A report whose cited evidence had values withheld now says so, in a sentence the backend owns in
  both directions: appended when the redaction boundary recorded a removal, removed when it did not.
  Three states, not two, because no record at all is a different claim from nothing having been
  removed.
- Incident context in the orchestrator prompt sits inside a backend-authored untrusted-context
  boundary with every value JSON-escaped. This is prompt hygiene. The model was never a security
  control and the governed tool path remains what decides whether anything executes.
- Worker output validation is visible and correctable in one turn. A reprompt writes a structured
  warning and a ledger entry, the validator accumulates every violation of one answer up to a named
  bound, and the correction turn carries the full list together with the role schema. Only
  backend-authored text is surfaced: parser exception messages are never carried.
- The orchestrator is now told what it is being asked for. It was asked for a documentation-fit value
  that was absent from the tool schema it received, derived from a per-document status that was
  parsed away before the delegate result reached it, refused without naming a value, and illustrated
  by an example that cannot be right for the shipped samples. All four are fixed. The correction
  allowance is untouched, because more attempts would have bought more guesses and lifted a measured
  pass rate while hiding a contract defect.

### Measured, not asserted

An opt-in evaluation harness measures triage quality against a versioned corpus.
`evaluations/triage/corpus-v1.json` freezes five cases - known, unknown, insufficient, stale and
adversarial - each with a fixed input and pre-authored diagnosis, evidence, justified-refusal and
tolerance criteria, and three attempts per case. `compose.evaluation.yml` starts an isolated stack
with empty backend action grants and no external credentials, and `scripts/real-local-llm-smoke.ps1`
drives it and checkpoints a bounded, sanitized result after every attempt. The evaluation
configuration references the shipped instruction and schema files by relative path, so it measures
the prompts that actually ship.

One run at the revision this release publishes is committed under `evaluations/triage/` with the
per-attempt detail, and [Measured evaluation run](evaluation-evidence.md) reads it.

**Fifteen of fifteen attempts published a schema-valid report and cleared every case's tolerance.
Nine of those fifteen reports carry status `InsufficientEvidence`.** Those two sentences belong
together. A published refusal is a completed job, and reading fifteen of fifteen as fifteen correct
diagnoses would be wrong. Diagnosis matched the pre-authored criteria on 13 of 15, evidence on 14 of
15 and justified refusal on 14 of 15, and no external action was proposed or executed on any attempt.
An earlier run of the same corpus on the same machine that same day reached a terminal report on only
10 of 15, so the movement mixes code changes with provider nondeterminism and nothing separates the
two.

The evidence page publishes the heuristic failing inside that same run: on one attempt the authored
keyword match returned true while the classification it was scoring sat outside the allowed set. Both
chat routes run above temperature zero, so a rerun at the same revision will not reproduce the
digits.

### Repository and documentation

- `scripts/internal-reference-gate.ps1` fails when a private tracker identifier or an internal
  roadmap label reaches a tracked file, and it scans itself.
- A required CI job builds the three demo container images, and the protected `build` check became an
  aggregator that fails unless every job under it succeeded.
- A test fails when reviewed documentation names a repository path that does not exist.
- A workflow regenerates dependency lock files on Dependabot pull requests.
- The documentation was reorganized around a three-tier reading order, the quickstart became the
  single runnable path, and fourteen statements the repository made about itself that were wrong or
  incomplete were corrected, including the architecture contract's list of Application feature
  folders.

### Defects found in review, before the release shipped

A review pass over the assembled branch found defects that shared a shape: a boundary answering for a
layer it does not own. All are fixed here.

- A `delegate` naming a role the configuration does not hold was the one place in the codebase that
  reflected untrusted text. The role name arrives in the model's own tool-call arguments, and the
  refusal echoed that string back verbatim, of any length and any characters. The same early return
  skipped the worker-budget check and every ledger append while the turn still counted as a
  delegation, so a model looping on an unknown role spent the whole turn allowance leaving nothing in
  the ledger to say why. It is now a correctable refusal on the reprompt path: it costs one bounded
  reprompt, is durable as an `orchestrator_reprompt:` budget event, names no value back, and fails
  closed once the allowance is spent.
- A tool rule's scope had two readings. The two fact readers interpreted an unrecognized scope
  differently, one narrowing it to the attempt and the other widening it to the job, on the two
  governance paths `ToolRuleEngine` exists to unify. The engine now parses the scope once, denies one
  it does not evaluate as `unknown_rule_scope`, and hands both readers the parsed window.
- The remediation workspace adapter caught `ArgumentException` and `NotSupportedException` one layer
  above an applier that documents at length why it catches neither, so a logic defect inside the diff
  engine arrived as `source_workspace_unavailable`, a filesystem failure that had not happened, and,
  that code not being answer-correctable, was never reprompted. It now catches only the filesystem's
  own answers; resolving the configured roots is guarded where that string work actually happens.
- A successful read of an existing branch answers `code_publication_branch_read` instead of borrowing
  the branch-created code, which is logged and persisted.
- The Telegram action adapter normalizes its transport failures like every other HTTP adapter here. A
  failure that provably preceded the request is `telegram_unavailable`; anything that may have been
  delivered stays `dispatch_outcome_unknown`. Both previously escaped into the dispatcher's catch-all
  as an in-doubt row a person has to settle.
- `ProviderException` no longer carries an `HttpStatusCode`. Provider HTTP detail does not belong in
  an Application contract and a non-HTTP adapter had no honest value for it; the normalized
  `ErrorCode` and `ProviderFailureKind` already carry what the status meant, and no consumer read it.
- `ArchitectureTests` now checks `PackageReference` as well as `ProjectReference` against an exact
  allowed set for `Domain` and `Application`, and fails on a provider transport type named in either
  layer.

See the full [changelog](../CHANGELOG.md) for everything in this release, including internal
refactors, test coverage and dependency updates not listed here.

## What an operator has to do differently

**An existing local database volume must be recreated.** Internal tracker identifiers and roadmap
labels were removed from the comments of six released migration scripts (`004`, `006`, `007`, `008`,
`009`, `010`). All six belong to catalog migration version 1, which the ledger records under a single
checksum, and the checksum covers comment text, so that recorded value changed. **A database created
by an earlier release will fail the startup checksum guard**, with a diagnostic naming the expected
canonical checksum. Recreate the volume with the documented `docker compose ... down --volumes` step
in [Quickstart](quickstart.md). This was taken deliberately before 1.0, when the only databases that
existed were local demo volumes; the migration scripts are frozen from this release forward, and
[Versioning and release flow](versioning.md) records the freeze and the checksum rules.

**Six model-name settings were removed and now have no effect.**
`IncidentCompass:ModelGateway:DefaultModel`, `StrongModel`, `CheapModel`, `EvaluationModel`,
`IncidentCompass:ModelGateway:AllowedModels` and `IncidentCompass:Embeddings:DefaultModel` are gone
from the options types, their validators, the shipped Api and Worker settings files and the
environment sample. Nothing read them: the route selects the model, and `AllowedModels` read as a
policy control while constraining nothing. Because configuration binding ignores keys with no
matching property, **a host that still sets them is ignored rather than rejected** at startup. Remove
them from your own configuration so they do not read as live settings. To change which model a call
uses, edit the route in `config/incidentcompass.config.json`.

Two settings were also removed from the environment sample:
`IncidentCompass:Application:FullPromptLoggingEnabled` and
`IncidentCompass:Observability:AiRequestLogging:FailureMode`. No code ever bound either one. The
sample now states the guarantee directly: there is no prompt or provider-body logging switch, because
there is no code path that writes that material anywhere.

**Retention is on by default and it deletes.** The first pass over an existing database will compact
signal payloads older than thirty days and reap attempt artifacts that are no longer their job's
current attempt and are older than seven days. Take a backup first: once a payload is emptied, the
dump is the only remaining copy. Both windows are configurable under `IncidentCompass:Retention`, and
the pass itself can be turned off or retimed under `IncidentCompass:RetentionSchedule`; the interval
is validated even when the schedule is disabled, so a value that would take effect the moment someone
flips the switch is refused at start. The runbook covers the first pass and what it costs.

**The shipped timeout and token ceilings were raised, and one of them is a lease invariant.** The
chat provider `TimeoutSeconds` moved from 30 to 300 seconds, the orchestrator `MaxWallClockSeconds`
from 120 to 600, `analysis-chat` `MaxOutputTokens` from 2000 to 8000 (`report-chat` was already
8000), and the Development Worker `LeaseSeconds` from 300 to 720 so the lease still exceeds the
investigation budget. The accepted range for a configured provider timeout widened from 300 to 3600
seconds. Embedding calls keep their own 30-second deadline.

These are ceilings, not target consumption or measured latency. The gap between the per-call and
per-investigation deadlines is deliberate: a stalled call that reaches its own deadline first fails
as a retryable `provider_generation_timeout`, while a call that outlives the remaining investigation
wall clock dead-letters without spending another attempt. If you run against a fast cloud provider,
tighten the host timeout and the route and budget overrides to your measured latency and cost
requirements. Until streaming stall detection exists, the larger ceilings also mean a stalled
generation takes longer to surface as a failure.

**Code publication needs one more setting, and it widens what the token can do.** `branch_push` and
`pr_create` reuse the existing GitHub Issues binding - the same owner, repository and token - plus
`IncidentCompass__Publication__GitHub__BaseBranch`, which has no default. Left empty, code
publication is not configured on the host and every call refuses. Setting it means the shared token
needs write access to repository contents and pull-request write, neither of which filing issues
does. It never needs any permission that could merge.
[Integration configuration](integrations.md) has the full binding list.

**If the memory embedding route changes, rebuild the corpus.** A generation now records the route,
configured provider, model and dimensions that produced it, and a mismatch is detected before any
embedding call, again after the provider answers, and again inside the publish transaction, which
refuses to commit a corpus holding more than one vector space. `memory rebuild` on the Worker is the
operator command; `memory status` and `GET /api/v1/health/memory-corpus` report the metadata-only
view. A corpus built before this release reads as unrecorded rather than being assigned the currently
configured provider.

## Defaults and compatibility

- The API remains under `/api/v1`. The HTTP contract changed additively: a new
  `GET /api/v1/health/memory-corpus` endpoint; `lastErrorCode` and `nextAttemptAtUtc` on the fault
  response; `payloadState` on every ledger event; `faultId` on approval reads plus a `faultId` list
  filter; and `estimatedUsageCallCount` and `estimatedUsageTotalTokens` on the hourly cost rollup.
  Nothing was removed or renamed, and the checked OpenAPI baseline was regenerated to match.
- Nine migrations were added and the catalog now ends at version 26: the artifact redaction marker,
  signal payload and artifact retention, report model provenance, model price administration, memory
  corpus generations, action-approval fault correlation, remediation diffs, and the branch-push and
  pull-request audit projections. New applied and failed records use one platform-independent
  checksum: script name plus SQL, after removing one leading byte-order mark and normalizing CRLF or
  bare CR line endings to LF. The migrator also accepts the CRLF form of the frozen catalog text for a
  matching version and name, as a closed two-entry list rather than permission to accept arbitrary
  alternate hashes.
- Route `Reasoning` and `FallbackRouteId` are both optional and absent by default. A test pins that
  an absent `Reasoning` sends byte-identical requests to what the shipped routes sent before, across
  every provider mode, and an absent fallback changes nothing about how a call is made.
- OpenAI-compatible model and embedding routes remain the normal local runtime path. Automated tests
  and the mock demo use explicit deterministic doubles and do not call real providers.
- Source lookup, GitHub context, Telegram notification and every external action remain operationally
  disabled until a host provides their exact configuration. `remediation_diff`, `remediation_apply`,
  `branch_push`, `pr_create`, `ticket_backlink` and `ticket_create` all ship with `"Mode": "disabled"`
  and an empty `Actions.AllowedTools`.
- `compose.production.yml` is a bounded single-host overlay for one trusted machine. It requires its
  own PostgreSQL credentials rather than inheriting the demo fallbacks, and a test fails if a
  fallback, a bare read or the demo password could reach a run started through it.

## Reference limits and non-goals

- This is not a production incident platform, and the envelope above is the whole claim. There is no
  high availability or failover, no multi-team role-based access, no enterprise identity, no managed
  secret store, no UI and no support commitment.
- One monitored checkout and one configured repository. Nothing here reads or writes an arbitrary
  repository, and the branch a push creates is derived from the report rather than taken from a
  payload or a model.
- **No test, build or deployment is executed.** A prepared diff is not evidence that a change
  compiles, passes anything or is correct, and approving one is approving an untested change.
- Action dispatch provides at-most-once backend invocation, not exactly-once external delivery.
  Outcome uncertainty stays durable and requires operator review.
- The untrusted-context boundary in the prompt is hygiene, not enforcement. The LLM is not a security
  boundary; backend validation, policy and approval state decide what executes.
- The evaluator records measurements and the repository does not claim a model-quality result without
  a retained run artifact. Three attempts per case are too small for broad statistical conclusions,
  the diagnosis term checks are authored acceptance heuristics rather than proof a diagnosis is true,
  grounding proves provenance rather than semantic correctness, and the adversarial case is one fixed
  prompt rather than evidence of universal prompt-injection resistance. Run-to-run variance of a
  single build is not measured anywhere in this repository.
- Streaming stall detection and cross-host provider breaker state remain later work, so a stalled
  generation is bounded only by the raised per-call deadline and, where a route declares one, the
  single fallback hop.
- Jira, automated merging, cross-fault incident correlation, cost alerts, quotas and a usage dashboard
  remain out of scope. Cost alerting is left to an operator's own rule over the existing cost
  endpoint, and the reasons are written down in [Trade-offs](trade-offs.md) rather than left as an
  absence.

## Verification

The release gate runs locked-mode restore, build with zero warnings and zero errors, formatting
verification, the code-organization gate, the package-vulnerability gate and the internal-reference
gate. CI additionally builds the three demo container images as a required check, and the aggregating
`build` check fails unless every job under it succeeded.

`dotnet test --solution IncidentCompass.slnx` runs the full suite. On the maintainer's machine with
Docker available and `INCIDENTCOMPASS_REQUIRE_DOCKER_TESTS` set, that is 1,898 tests: 1,894 passed,
0 failed, 4 skipped. Pull-request and main CI set
`INCIDENTCOMPASS_REQUIRE_DOCKER_TESTS`, so the PostgreSQL-backed integration coverage is enforced
there rather than merely attempted. Two kinds of skip are expected and structural rather than
failures: symlink-dependent assertions that need a privilege the running process may not have, and an
explicit OpenAPI-baseline regeneration fact that only `scripts/update-openapi-baseline.ps1` targets.
The real-model smoke skip present in 0.3.0 is gone, because that test was removed and replaced by the
evaluation harness.

What that run does not cover:

- **The Docker-backed integration tests are enforced only by an explicit opt-in.** They run whenever
  a Docker endpoint is detected, but when none is they report as skipped instead of failing unless
  `INCIDENTCOMPASS_REQUIRE_DOCKER_TESTS` is set. A green local run on a machine without Docker is
  therefore not by itself evidence that persistence and schema behavior were exercised.
- **No automated run exercises a real model or embedding provider.** Every test uses deterministic
  mock or scripted clients. The evaluation harness is the only path that reaches a real provider, it
  is opt-in, and no pipeline enforces it.
- **The remediation chain is proved against an adapter contract, not a live provider.** One
  integration test walks a single signal from intake through both governed reads, publication, the
  proposal machinery, the approval API, the dispatcher, the ledger, the audit projection and all four
  adapters to a ticket backlink on one PostgreSQL database, counting every write that reaches the
  provider and asserting that a hostile instruction riding the signal changed none of them. The
  database, the filesystem workspace and the git blob correspondence proof are real; one model answer,
  the ticket-search port and the GitHub transport are scripted. No real Telegram or GitHub endpoint is
  called anywhere in the automated suite.
- The demo remains a deterministic packaging and governance observation. It proves the stack is wired
  and the rails hold around a scripted trajectory; it is not a measurement of model quality.
