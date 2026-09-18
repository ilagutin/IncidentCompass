# IncidentCompass 0.5.0 - local embeddings and a relevance judge, sectioned memory, and limits that fit a slow model

IncidentCompass 0.5.0 changes four things an operator meets on the first day. Memory embeddings no
longer need an external embedding server: the Worker runs a pinned multilingual model in process and
installs it itself. Memory search no longer decides relevance by counting words: a second in-process
model, a multilingual cross-encoder, reads the query together with each retrieved candidate and
decides which documents come back at all, and then reads each returned document against the incident
as its trigger signal describes it to decide which of them are confirmed, so a Polish or Russian
incident reaches an English runbook, an off-topic query in any of the three reaches nothing, and the
model cannot confirm a document by searching again with its wording. A slow local chat model no
longer fails an investigation at a short total deadline: each provider call is bounded by what it is waiting for, chat answers are streamed by
default so a stall is visible as a stall, and the attempt as a whole gets a four-hour safety ceiling.
And an investigation now polices its own tool use and its own progress: every tool call has its own
execution limit, repeated calls and stalled progress are detected, a bounded recovery call gets one
attempt to unstick it, and a stalled investigation still ends with an honest report the backend writes
itself.

This is still reference-quality software with the single-host envelope described in
[Single-host production runbook](single-host-production.md). Nothing below widens that envelope.

## What changed

### The Worker is the only embedding host

The embedding client, the memory seed and resync pass, `memory rebuild` and the `memory_search` tool
are composed only by the Worker. The API keeps the corpus status and health readers, which need no
model, and no longer runs the corpus commands; `memory status`, `memory rebuild` and the new
`memory model` commands run in the worker container. The API image no longer carries the sample
runbooks, and the API service no longer sets a seed source directory it never used. An architecture
test fails if API source names an embedding adapter, the embedding port or the corpus command.

### An in-process embedding model is the shipped default

A new provider kind, `LocalOnnx`, runs `intfloat/multilingual-e5-small` inside the Worker with ONNX
Runtime and the model's own SentencePiece tokenizer. The model is MIT-licensed and pinned to one
Hugging Face revision; its int8 ONNX file and its tokenizer are each pinned by SHA-256. The adapter
applies the model's `query: ` and `passage: ` prefixes, caps input at 512 tokens, mean-pools,
normalizes and returns 384 dimensions. Every embedding request now states whether it carries a query
or a passage; the OpenAI-compatible and mock adapters ignore that field.

The shipped configuration routes `memory-embed` to a `local-embed` provider of kind `LocalOnnx`, and
both Compose files select that host provider unless `INCIDENTCOMPASS_EMBEDDINGS_PROVIDER` says
otherwise. The OpenAI-compatible embedding adapter stays available as an operator choice, set with
three values together (host provider, provider id and model), and the evaluation stack keeps it.

The model lives in the `embedding-models` volume at `/app/models`, mounted on the worker only. On
start, before the memory seed pass, the Worker installs or verifies it:

- An installed manifest wins. Both files are hashed again on every start, and a changed host default
  never replaces what is installed.
- A file already at its artifact path, for example one placed offline, is verified and used, never
  fetched over.
- A missing file is downloaded over HTTPS within a size limit to a temporary file, hashed while it is
  written and renamed into place only when the digest matches. The manifest is written last.
- A mismatch or failure is refused with a named code and never repaired. It does not stop the Worker.

A corpus embedded by the local model records its model as
`<model id>@sha256:<first 16 hex characters of the model file digest>`, so a model file replaced under
the same id is detected as an embedding route change. When the installed model cannot serve the
route, the Worker reports `memory_embedding_model_mismatch` (a model is installed but its id is not
the route's model) or `memory_embedding_model_unavailable` (no usable model is installed). Both publish
nothing and keep the previous corpus current, and `memory rebuild` refuses in either state because a
rebuild cannot repair it.

Two Worker commands manage the model. `memory model status` prints the installed model's id,
revision, license, encoded identity and digests beside the configured route and the corpus model, and
exits 1 on any mismatch. `memory model install` installs the configured model beside the current one
within the install timeout, keeps the replaced manifest as `manifest.previous.json` and never deletes
an installed file, so an install can be rolled back by restoring the previous manifest and restarting
the Worker. The runbook gives the install, offline install and rollback procedures.

`THIRD-PARTY-NOTICES.md` lists the model and the runtime packages with their licenses, and a test
fails if a shipped license is anything other than MIT or Apache-2.0. The model itself is not in this
repository or in any image built from it.

### A model that cannot serve the route is a configuration failure

While the installed model does not match the route, or no usable model is installed, every triage job
that reaches `memory_search` has its embedding call refused. That is now treated as a configuration
failure rather than a provider outage: the attempt stores `memory_embedding_model_mismatch` or
`memory_embedding_model_unavailable`, with the adapter's own code kept as the provider error code,
spends the ordinary attempt budget and is dead-lettered when that runs out. It never pauses job claims.

**A failed download or an install timeout of the local model now spends job attempts instead of
pausing claims.** Either one leaves no usable model installed, so a job that reaches `memory_search`
fails its attempt with `memory_embedding_model_unavailable` and the Worker keeps claiming work. A
model that is installed but fails to load, and an unreadable embedding configuration, still count as
provider outages.

### Memory documents are chunked by section

The seed pass no longer embeds a file as one chunk. It splits the frontmatter-free body on ATX
headings, ignoring heading-like lines inside fenced code, and caps each section by tokens on whole
lines, preferring paragraph boundaries, with a small overlap between adjacent chunks. The defaults
under `IncidentCompass:Memory:Seed:Chunking` are `MaxTokens` 448, `OverlapTokens` 48 and `MinTokens`
32. Token counts come from the installed model's own tokenizer for the local adapter, and from an
estimate of one token per four characters for the OpenAI-compatible and mock adapters, which expose
no tokenizer. The Worker refuses to start seeding when the chunk cap plus the prefix and sequence
markers would exceed the installed model's window.

Every chunk starts with its heading path, the ancestor headings joined with ` > `. The path is stored
in `memory_chunks.heading_path` and returned as `headingPath` in `memory_search` output. Chunks remain
the retrieval unit, so two sections of one runbook can both match. A heading path or a single line that
cannot fit the cap is refused rather than split or truncated, and the pass fails naming the seed
file's relative path without quoting it.

Each corpus generation records its chunk policy in `memory_corpus_generations.chunk_policy`, for
example `v1;max=448;overlap=48;min=32;exact`. A changed chunking setting publishes nothing, keeps the
previous corpus current and reports `memory_chunk_policy_changed` until `memory rebuild` publishes a
generation under the new policy. Migration `036-memory-chunk-structure.sql` adds both columns as
nullable and rewrites no row.

**A corpus seeded before migration 036 keeps its whole-file chunks and is re-chunked only after
`memory rebuild`.** Such a generation has no recorded policy, which reads as unrecorded rather than as
a different policy, so it stays current and its unchanged files are not re-chunked by the ordinary
seed pass.

### The local models were measured, and the shipped settings stay

An opt-in benchmark, run only when `INCIDENTCOMPASS_EMBEDDING_BENCHMARK` is set, runs the retrieval
corpus through the real local adapter for `multilingual-e5-small` and `multilingual-e5-base`, with the
English queries and Polish and Russian renderings that reuse the same relevance labels. It reports
the production `memory_search` pipeline across `MinScore` values, the same pipeline under each
`VectorOnlyFallback` mode, the raw vector ranking and query embedding latency. The deterministic gates
keep the mock embedder.

The measurement keeps `multilingual-e5-small` as the shipped model and `MinScore` at 0.25. It was
taken on the smaller 12-item corpus, which this release then grew; the leg itself now runs on the
24-item corpus the judge section describes. On that
model the floor removes nothing: every relevant match scored well above it, and so
did unrelated chunks. What keeps unrelated chunks out is the reranker's lexical coverage rule on a
host that runs no relevance judge, and the judge itself on a host that runs one.

### Memory search reads another script

This is the lexical path, and it is what decides admission on a host that runs no relevance judge.
Where a judge is installed, the judge decides instead and the next section describes what it does.
The lexical path admits; it never confirms.

Lexical coverage is now judged per candidate over the query words that candidate could carry at all.
A counted query word is eligible when it has no letter, such as a number, or when its writing system
occurs among that candidate's own words; a word in a script the candidate never writes is left out of
the judgement instead of counted against it. For a query and a corpus in one script the rule is
unchanged. A mixed query such as `circuit breaker при задержках склада` is now carried by its two
Latin words over an English corpus.

`Tools.memory_search.VectorOnlyFallback` decides what happens when coverage leaves nothing: `off`
returns nothing as before, `always` returns the top `TopK` candidates unconfirmed, and the default
`foreign_script` does so only when the query uses a writing system no candidate writes. The key is
optional, no shipped or sample configuration sets it, and it cannot reach below `MinScore`. On the
smaller 12-item benchmark corpus these three figures were measured against, Russian pipeline recall@5
was 0 before this release; eligibility alone raises it to 0.17,
because it admits the one query that keeps its Latin identifiers, and the default fallback takes it
from there to 1.00 over that corpus's six positive queries. English stays exactly as it was. **On a
host that runs no relevance judge, a language in the same script as the corpus, such as Polish over
English runbooks, still finds nothing by default**;
`always` recovers it, at the price of unconfirmed items for every query without lexical support,
English off-topic queries included. The default also compares scripts against the whole candidate set,
so a candidate set that holds even one chunk in the query's script turns the default fallback off for
that query. See [Trade-offs](trade-offs.md).

`retrievalConfidence` now names whether a returned document was confirmed as describing the incident
rather than where its vector score fell, and only a relevance judge can confirm one. On an unjudged
call every item is `low`, whatever its lexical coverage, and a lexically admitted set carries the
top-level `message` `related matches, none confirmed by the relevance judge`. A vector-only result
carries its own top-level `message`, `vector-only matches, not lexically confirmed`. The sections
below say how a judged call bands an item. The numeric `score` is unchanged. The memory role is
instructed to keep identifiers verbatim, to search with the fault's service, error type, message and
route, and to drop a `low` item whose quote is not about the fault. The role is told it may write the
query in the incident's own language, because the judge described next reads across languages.

### A relevance judge decides what memory search returns

The Worker now runs a second in-process model, and it is not an embedding model.
`BAAI/bge-reranker-v2-m3` is a cross-encoder: it reads one query together with one candidate chunk
inside a single 512-token window and returns one score for that pair, instead of producing a vector
that is later compared with another vector. It is Apache-2.0 and pinned by revision and SHA-256 as two
files, an int8 ONNX export of 570,727,094 bytes, about 544 MiB, and the XLM-RoBERTa SentencePiece
tokenizer. The tokenizer is byte-identical to the embedding model's, so the same digest is pinned in
both sections; each model directory still installs its own copy. The ONNX export repository declares
no license of its own and names only its base model, so the export is taken under the base model's
Apache-2.0 license, and `THIRD-PARTY-NOTICES.md` records that reading together with both source URLs.

The judge has no provider entry in the triage configuration, no route and no `ModelCall` row. It is
one Application port, `IMemoryRelevanceJudge`, and the Worker host setting
`IncidentCompass:RelevanceJudge:Provider` chooses its adapter: `LocalOnnx`, the default, is the
in-process cross-encoder described here, and `Mock` is the mock stack's deterministic stand-in,
described below; a value that is not one of those two provider kinds, in any casing or spelling the
provider parser accepts, stops the Worker at start. With the local adapter,
`IncidentCompass:RelevanceJudge:LocalOnnx:ModelDirectory` decides whether the host runs a judge at
all: an absolute path turns it on and has every other judge setting validated before the host starts,
and a blank or absent value leaves the host without one rather than failing a start over a model the
host was never asked to run. `docker-compose.yml` and `compose.production.yml` set it to
`/app/models/relevance-judge`, inside the existing model volume, so one volume holds both models and
one `memory model install` fills both. `compose.evaluation.yml` inherits that directory, so a
real-model evaluation runs the product as shipped, judge included. `compose.mock.yml` selects the mock
judge and empties the directory, because that stack mocks every model and downloads none. The judge
is composed only where the embedding host is composed, which is the Worker; the API loads neither
model, reports nothing about the judge and no health endpoint carries its state.

Installation and verification follow the embedding model's rules exactly, and the pass runs last on
the Worker's start, after the embedding install and after the memory seed pass, so the corpus the
Worker serves is never held up behind the larger download, and still before the claim loop, so a
Worker that has started has either verified its judge or recorded why it could not. One pass is
bounded by `InstallTimeoutSeconds`, 1800 seconds by default rather than the embedding model's 900
because the file is about five times the size. A failed pass does not stop the Worker: it records its
code, logs it, and judge calls are refused with that code until a later start succeeds; a
`memory model install` run beside it fills the volume, and the running Worker keeps its recorded
failure until it is restarted.

**Where a judge is installed, it and not the lexical coverage rule is the admission authority, for
every query rather than only for a query the gate emptied.** The gate's worse failure is the query it
passes: one whose eligible words are all Latin identifiers every candidate carries, where coverage is
complete for that reason alone and an unrelated chunk is admitted; until confirmation moved to the
judge, it was also reported as a confirmed match. The lexical path now bands every match it admits
`low`. The judge scores each candidate against the model's query, and a candidate below
`Tools.memory_search.RelevanceFloorScore` is dropped outright; the rest are admitted and ordered. The
returned documents are then scored a second time, against a query the backend builds from the
incident, and a document at or above `Tools.memory_search.RelevanceConfirmScore` on that second score
is confirmed; the next section describes that query. The default floor is -0.25 and the default
confirm score 0.85, both measured through the product, the confirm score against the backend's query
because that is what it now decides. Both are code defaults: no shipped or sample configuration sets
either of them, or `Tools.memory_search.RelevanceJudge`, which takes `off` or `on` and means `on` when
absent.
Configuration load refuses an unknown mode, a score outside -50 through 50 and a floor that is not
below the confirm score. `VectorOnlyFallback` does not apply on a judged call, because there is no
lexical gate left to leave anything empty.

`on` does not mean required. Exactly two states are a host that is not running a judge at all: no
judge model directory is configured, and nothing is installed in the configured one yet. Those keep
the pre-judge admission path, confirm nothing, and say so in a new top-level `limitation` string, so a
reading role is not left assuming a result was judged. Every other failure propagates with its own
error code and nothing falls back: a digest mismatch, a missing file, a failed fetch, an oversized
download, an install timeout, an invalid manifest, an unreadable store, an installed judge that is not
the configured one and a failure at load or inference are all a broken host rather than a judge-less
one, and answering such a call from the lexical gate while reporting that no judge is installed is the
silent degradation the judge exists to remove. An install still running is refused the same way,
with `relevance_judge_install_in_progress` as the adapter's code. What such a refusal does to the job
depends on its kind, and the upgrade notes spell it out: a judge that is not installed, is still
installing, failed its install or does not match is a configuration failure that spends the job's
attempts, and a judge that is installed but fails to load is a provider outage.

**`RelevanceJudge` set to `off` is an unjudged call like any other.** It admits on the lexical path,
confirms nothing, and carries the same `limitation` string, `no relevance judge ran, so matches were
admitted by lexical support alone and none was confirmed`, so a reading role can tell an `off` result
from a judged one. A judge that answered the admission call and then reports itself absent on the
confirmation call leaves every item `low` too, with the limitation `no relevance judge ran to confirm
these matches, so none was confirmed`.

On a judged call the band a match carries never claims more than the weaker of two judgements says,
and both are taken against the backend's query rather than the model's: `high` is the judge
confirming the document with every counted word of that query also in it, `medium` is the judge's
confirmation alone, and `low` is admitted but unconfirmed. `memory_search` reports the judge's
admission score against the model's query as `judgeScore` and its confirmation score against the
backend's query as `confirmationScore`, beside the unchanged vector `score`; each is null when nothing
judged it. The top-level `message` reads `matches found` only when at least one item is confirmed,
which needs a judge; a judged set in which nothing reached the confirm score, and every lexically
admitted set on an unjudged call, carries `related matches, none confirmed by the relevance judge`,
which is a different sentence from the unjudged fallback's. Ordering keeps the combination the
reranker already applied, with the judge's admission score substituted for the vector score as the
base term. The memory role copies `retrievalConfidence` into every item it returns, and that field is
now required rather than optional in the role's output schema.

Because the judge reads the query and the candidate in one window, `memory_search` now refuses a query
longer than 1016 characters as `invalid_arguments`. The number is derived from the window rather than
chosen: half of what the 512-token window leaves after the pair's four markers, at the same
conservative four characters per token the chunker already estimates with.

### The model store installs any pinned artifact set

The local model store was written for the embedding model and now installs any pinned artifact set,
because a pinned artifact set is a pinned artifact set whatever it is for. A manifest records the kind
of model its directory holds, `embedding` or `relevance_judge`, beside the id, revision, license, run
settings and each file's path, source URL and digest, and the embedding-only settings are required of
an embedding manifest and absent from every other kind. A directory holding the wrong kind is reported
as exactly that rather than loaded as something it is not. The earlier embedding-only manifest, which
carries no kind field, is still read as the embedding manifest it always was, so a model directory
filled before this change is not orphaned.

The two models need two directories because a directory holds one active manifest, not because they
are kept apart. Both directories hold the same three things: `manifest.json`,
`manifest.previous.json` and each file at `artifacts/<its SHA-256>/<its file name>`.

Both `memory model` commands now cover both models. `memory model status` prints the installed judge's
id, revision, license and digests after the embedding model's report, and `memory model install`
installs the judge in the judge's directory under the judge's own timeout and prints where the
previous judge manifest is kept. Each failure is reported rather than thrown and the exit code is the
worse of the two, so a broken judge never hides the embedding model's report. **Both commands exit 1
on a host that configures no judge directory**, where they previously exited 0, and on a Worker that
runs the mock judge, which they report as the mock.

### A known incident may not rest on memory nothing confirmed

A `Completed` report classified `KnownIncident` that cites at least one memory-backed retrieved
document must cite at least one whose recorded band confirms the match, and is refused otherwise with
one fixed backend-authored sentence pair that carries no artifact id, title or quote. No ticket,
remediation or post-report workflow reads the classification. The bar exists because of what the
classification tells a person: it is part of the report the approver of a ticket, a branch push or a
pull request reads when deciding, and `KnownIncident` tells them this failure matches one already
documented. The consumer of a retrieved item is a model rather than a person who would notice the
difference between a document about this subsystem and a document about this failure, so the backend
has to be the one that tells them apart.

The rule is scoped by the cited artifact's memory item id, not by the evidence kind. A ticket-search
or source-lookup result is also stored as a `RetrievedItem` and its closed payload carries no band at
all, so a `KnownIncident` grounded on one of those stays publishable, as does a report citing no
retrieved document. The durable `ToolResult` of a `memory_search` call is memory-backed too, and it
carries no band at its top level, which is the only place the publication rule reads one: its payload
is the tool's whole output, so the per-item bands inside it are one level down and are never read, and
a `KnownIncident` cannot rest on that artifact alone. **A memory payload with no
band recorded does not count as confirmed**, which is what an artifact written before this release
looks like: the safe reading of that silence costs one correction turn on a re-triage, where reading
it as confirmation would tell the report's reader the failure is documented on the strength of a
match nothing ever judged.

**Without a relevance judge nothing is confirmed, so no memory-based `KnownIncident` report can be
published.** On a host with no judge, with `RelevanceJudge` set to `off`, or when the judge becomes
absent between the admission and the confirmation call, every item is `low` and the result says why
in its `limitation`. Documents are still retrieved, admitted and passed on as context, and
`VectorOnlyFallback` still governs admission; only confirmation is withheld. Word overlap was
considered as a stand-in and rejected: a short incident description confirms on a single shared word,
and one written in another script leaves only its Latin identifiers to match, so a document would be
confirmed on the service name alone. The shipped compose files run the judge, and the mock stack runs
the mock judge described below. The upgrade notes say what that changes for an existing deployment.

So that the orchestrator can meet the bar rather than only be refused by it, the band travels to it.
The memory role copies `retrievalConfidence`, the delegate result carries it, and the shipped
orchestrator instruction states the rule in terms of that field. Handing a model a
governance-relevant field forges nothing: the refusal is decided against the band `memory_search`
stored on the artifact, never against the worker's copy of it.

### A document is confirmed against the incident, not against the model's query

The model writes the `memory_search` query, so a band decided against that query is a band the model
can raise: a model that searched again with a document's own wording would get that document
confirmed, whatever the fault was, and a `KnownIncident` could then rest on a document the incident
never described. Admission and ranking still answer the model's query, because that is what it is
looking for. Confirmation answers a different question, whether the document describes the incident,
so it is a second judgement of the returned documents, and only those, against a query the backend
builds from the incident's trigger signal: the service name, the error type, the error message and the
HTTP route, in that order, joined with one space and with blank parts skipped. It is held to the same
1016 characters as the model's query, but where the model's query is refused past that bound, the
backend's query is cut to it on a rune boundary. When the error message is blank, which is every
user and manual report, the sender's summary takes its place, but never the summary intake
synthesizes for a structured signal that came without one. A re-triage confirms against the fault's original trigger signal. No model output reaches
those fields, so searching again with other words changes which documents come back and cannot raise
the band of a document the model has already been shown. The memory role and orchestrator
instructions say so, and the memory role is told to search with the route as well.

Two parts of that query were decided by measurement. The synthesized summary is left out because its
templated "failed" frame turns any signal into a failure of its service: with it in, an off-topic
checkout price-rounding signal scored 2.97 against the known checkout-timeout incident, above the
confirm score, where the same off-topic benchmark query scored -1.15 against that incident as the
role's own query. The route is in because it
is the sender's own data and gives a signal written in another language, or without its diacritics,
an anchor against English runbooks: without it, Polish incident-shaped positives were confirmed 4 of
6, and with it 6 of 6.

Confirmation costs one more judge call per `memory_search`, over the returned items only, at most
`TopK` pairs, and it fails exactly as the admission call does: every failure propagates with its own
code, and a non-finite score or a wrong score count is refused by name.

### The mock stack runs a mock judge

`IncidentCompass:RelevanceJudge:Provider` set to `Mock` composes a deterministic stand-in that needs
no model, so the mock stack, which mocks every model, takes the same judged path as the product and
can reach a memory-based known-incident report, which a judge-less host never can. Its rule is coarse
and reads identifiers only: a candidate that names an error type the query names is confirmed, one
that names only a hyphenated service name the query names is returned as related, and anything else is
dropped, at the default thresholds. `compose.mock.yml` selects it.

**The mock judge is not a governance boundary, and a confirmation it produces is indistinguishable
from a real one in the tool output and the stored artifact.** Four things stand between it and a
production host, two that refuse it and two that detect it. `compose.production.yml` pins
`IncidentCompass__RelevanceJudge__Provider` to `LocalOnnx` on the worker rather than reading it from
a variable, and `scripts/production-preflight.ps1` refuses an environment entry naming any other judge
provider and checks that the rendered worker's provider is `LocalOnnx`. A Worker that composes the
mock logs warning event 2323 on every start, and `memory model status` and `memory model install`
report a mock judge as the mock, never as a ready judge, and exit 1; neither of those two stops a
Worker from running it. A host that skips the preflight and adds its own override file is not covered
by the two refusals.

### The judge was measured through the real tool

The retrieval benchmark corpus grew from 12 items with 8 queries per language, 6 of them positive, to
24 items with 24 queries per language in three declared categories, 12
positive, 6 off topic and 6 hard negative, so a threshold could be measured rather than guessed. Every
number in this section is from the grown corpus, and both benchmark legs now run on it; the earlier
figures in "The local models were measured" and "Memory search reads another script" are from the
smaller one. The
measurement below runs the real `memory_search` over the grown corpus with the judge off and then on. The
off column is the shipped lexical behaviour with its default `foreign_script` fallback, not a stripped
pipeline. Recall@5 counts the positive category.

| Language | recall@5 off, on | off-topic false positives off, on | hard negatives confirmed off, on |
| --- | --- | --- | --- |
| English | 1.000, 0.958 | 0 of 6, 0 of 6 | 0, 0 |
| Polish | 0.000, 0.500 | 0 of 6, 0 of 6 | 0, 0 |
| Russian | 1.000, 0.500 | 6 of 6, 0 of 6 | 4, 0 |

The hard-negative column was measured while the band still answered the model's own query, and its
off side while an unjudged call could still confirm on lexical support. The band now answers the
backend's query, an unjudged call confirms nothing, and the confirm score was measured again for
that, below.

Russian is the row the judge was built for. The vector-only fallback was already carrying its positive
queries, and it was carrying every off-topic and hard-negative query with them, because a fallback is
not relevance. With the judge on, no off-topic query comes back and no hard negative is confirmed, in
any language. Polish is the case
the lexical rules could not reach at all, and it moves off zero for the first time.

No English positive query comes back empty with the judge on. The 0.958 is one query returning one of
its two labelled chunks, and the chunk it loses is the redundant second chunk of a query whose first
chunk is still returned. The two 0.500 figures are entirely the eight keyword-shaped queries carried
over from the older fixture, which are lists of terms rather than questions; on queries written in the
shape a model produces from an incident, recall@5 is 1.000 in all three languages. The keyword queries
are kept because they are the honest lower bound: a role that degenerates into keyword search gets
keyword-search results.

The floor is a choice between two things that cannot both hold, not a value sitting in a gap. On this
corpus the strongest off-topic pair scores above the weakest labelled relevant chunk, so no floor
returns every labelled chunk and also leaves every off-topic query empty; Polish and Russian have the
same shape, and no labelled chunk is missing from the candidate sets, so this is the judge's ordering
rather than a retrieval shortfall. The release takes the empty off-topic result, which is the stated
product requirement, and gives up the duplicate chunk. [Trade-offs](trade-offs.md) carries the two
bounding scores.

The confirm score does sit in a gap. It was swept through the real `memory_search` over the grown
corpus in all three languages, with the backend's query built from each benchmark query's own trigger
signal:

| Confirm score | Hard negatives confirmed | Incident-shaped positives confirmed |
| --- | --- | --- |
| 0.55 and below | 1 English | every one, in all three languages |
| 0.60 to 1.10 | 0 | every one, in all three languages |
| from 1.15 | 0 | Polish drops to 5 of 6 |

The window that keeps every hard negative unconfirmed and confirms every incident-shaped positive in
all three languages is (0.55, 1.10], and the shipped 0.85 sits in its middle. The earlier threshold
was measured against the model's own query and does not transfer to incident text. At 0.85 no hard
negative is confirmed in any language, English off-topic false positives are 0, and no English
positive query comes back unanswered.

The attack the change closes was measured the same way. For each off-topic and hard-negative query,
the model's query was replaced with the full text of the document the judge had scored highest for
it, which is what a model searching again with a document's wording does. Confirmed against the
model's query, as before this change, that confirmed 12 of 12 attack queries in each of English,
Polish and Russian. Confirmed against the backend's query at 0.85, it confirms 0 of 12 in each.

Both thresholds were measured through the product and nowhere else. An offline sweep of the same graph
under another ONNX runtime build put those two bounding pairs 0.167 apart in the opposite order, a
reordering larger than the whole window, so a threshold for this judge cannot be transferred from
anything but a run of the real tool.

The judge is what a `memory_search` now costs. On one desktop x64 processor, 20 scored pairs, which is
`TopK` times four at the shipped `TopK` of 5, took a median of 2247 ms at the shipped single intra-op
thread and 550 ms at eight, with bit-identical scores at both, so an operator with cores to spare can
buy the time back. Those 20 pairs are the admission call; confirmation is a second call over at most
`TopK` more pairs, five at the shipped setting, which the figure does not include. The 120-second default execution limit this release gives an immediate worker tool,
described above, was nowhere near approached.

### A non-Latin incident reaches the model as words

Every value a model read was serialized with the framework's default JSON encoder, which allows only
Basic Latin, so before this release every other character was sent as a six-character `\uXXXX`
escape. A Russian signal arrived as a run of `\uXXXX` sequences, six characters per Cyrillic letter: a
large model decodes that, a small local one may not, the prompt was several times longer than its
text, and the character-based prompt estimate charged the context window and the attempt's token
budget for the inflation either way. The orchestrator prompt, the worker prompt, the remediation
request, worker tool results and delegate results now carry a letter or a mark of any Basic
Multilingual Plane script as itself, and the 800-character artifact payload excerpt is cut after
decoding, so the same budget carries far more of a non-Latin payload than it did, short of the
characters listed below that are still escaped.

The untrusted-context boundary is unchanged, because the quoting is what the boundary rests on.
Untrusted text values are still JSON string literals, and quotes, backslashes, every control
character, the line and paragraph separators, every format character including the bidirectional
controls and the zero-width ones, every space separator other than the ordinary space, unassigned and
private-use code points and every supplementary-plane character all stay escaped: a message carrying a
newline and `END_UNTRUSTED_INCIDENT_CONTEXT` is still one quoted value on one line. Because the
zero-width joiner and non-joiner are format characters, a Persian word or an Indic conjunct that
spells itself with one is readable around that one escape rather than whole. Nothing durable moved
with it either. Hashes, fingerprints, stored artifact payloads, `ModelCall` metadata, the intake path
and the provider request body keep the previous encoding, and for ASCII text the two encodings are
byte-identical, so an English incident produces exactly the bytes it did before. What remains is
stated in [Security model](security-model.md): a look-alike letter from another script reads as itself
inside untrusted text as it would in any UTF-8 prompt, and a character that is invisible without being
a format character, such as a Hangul filler or a variation selector, now arrives raw. Neither can end
a quoted value or a prompt line.

The same rule now covers text one model call wrote that a later backend-authored prompt carries, which
three places used to carry raw: the name of a tool the orchestrator invented, the `task` a `delegate`
call names for its worker, and the suggestion a recovery review returns. Raw, each of them could forge
a line the backend appeared to have written - a context marker, a copy of the answer rule that follows
the context, or a whole second context block. Each is now one JSON string literal on one line, the
tool name is capped at 128 code units and named identically by the tool message and the reprompt, and
the worker prompt's label says the next line is the orchestrator's task as a JSON string. A delegated
task is still the instruction the worker follows; quoting only stops it forging structure, and the LLM
is still not a security boundary. The shared text cut also cuts on a rune boundary now, so a bound can
no longer leave half of a surrogate pair behind, which the model-facing encoder wrote as U+FFFD and a
PostgreSQL `text` column cannot store at all.

### Provider calls are bounded by phase, and the attempt by a long ceiling

The single chat `TimeoutSeconds` bounded a whole HTTP attempt, so a slow local model either needed a
very large value or failed as a timeout while still generating. The OpenAI-compatible chat adapter now
applies three limits to each HTTP attempt, under `IncidentCompass:ModelGateway:OpenAiCompatible`:

- `ConnectTimeoutSeconds`, default 30, for establishing the connection;
- `FirstOutputTimeoutSeconds`, default 600, from dispatch until output starts;
- `StreamInactivityTimeoutSeconds`, default 600, for silence once output has started, restarted on
  every output event, so a long answer that keeps producing is not cut off.

Each retry gets fresh limits. A limit that fires ends the call as a generation timeout recorded under
its phase, `provider_connect_timeout`, `provider_first_output_timeout` or
`provider_stream_inactivity_timeout`; `provider_generation_timeout` remains for an HTTP 408 answer. A
failure after the response started, or a cancellation none of the limits explains, is
`provider_dispatch_outcome_unknown` and is not replayed, because the provider may already have
generated.

`Orchestrator.Budget.MaxAttemptDurationSeconds` is the ceiling on one investigation attempt. Absent
means 14400 seconds, `0` disables it, and the shipped configuration sets 14400 where it previously set
a 600-second `MaxWallClockSeconds`. It is a safety net against a run that never ends; the call limits
are what catch a stalled call.

Each model call now asks the provider for at most the smaller of the route's `MaxOutputTokens` and the
tokens left in the attempt budget after the estimated prompt, sent as `max_tokens`. A call with nothing
left is refused before dispatch, and a route fallback skipped for that reason is recorded as a
`max_tokens_reached_before_call` budget event.

### Chat completions are streamed by default

The chat client requests `stream: true` with `stream_options: { "include_usage": true }` and assembles
content, tool calls, finish reason and usage from server-sent events into the same completion a JSON
body produces, which then goes through the same validation. The response's content type decides how
it is read, so a provider that ignores `stream` and answers with JSON keeps working.
`IncidentCompass:ModelGateway:OpenAiCompatible:Streaming=false` restores the previous request exactly.

A stream fails closed. An `error` event, a stream that ends before a finish reason and a body that
breaks off are `provider_dispatch_outcome_unknown` and are never replayed; only the provider error
code of an error event is kept, never its message. A malformed or inconsistent tool-call fragment is
`invalid_response`. Keep-alive comments do not count as output, so a provider that holds a connection
open while producing nothing is cut off at the inactivity limit. A chat response is capped at 32 MiB
and a single event line at 4 MiB characters, both ending the call as `provider_response_too_large`.
[Model gateway](model-gateway.md) lists the known provider incompatibilities, including
OpenAI-compatible layers that stream tool-call fragments without `index`, which need
`Streaming=false`.

### Every tool call has an execution limit

Each tool in the triage configuration may set `TimeoutSeconds`, from 1 through 3600. An immediate
worker tool that sets none is bounded at 120 seconds; an external action that sets none uses the
Worker's `IncidentCompass:ActionDispatch:AdapterTimeoutSeconds`, 30 by default. The shipped
configuration sets none, so it adds nothing to the shipped configuration's hash.

An immediate tool call runs under three bounds at once, and the outcome names the bound that fired:

- its own limit records a `Failed` tool result with `tool_execution_timeout`, and the worker continues
  with that limitation;
- the attempt ceiling ends the attempt with `triage_budget_wall_clock_reached_during_call`, as during a
  model call;
- shutdown propagates as cancellation without a ledger write.

Any other exception records a `Failed` tool result with `tool_execution_failed`, naming the tool, and
propagates unchanged. The executor stops waiting as soon as a bound fires, even for a tool that ignores
cancellation; such a read-only tool is abandoned and may keep running until it returns.

An external action resolves its limit before it is claimed, so the adapter deadline and the claim
deadline use the same value. An action stopped after its claim but before its adapter was invoked,
shutdown included, now closes as `dispatch_not_invoked`; after invocation the outcome stays
`dispatch_outcome_unknown` and the action is never re-sent.

### An investigation that stops making progress is detected and ended honestly

**Repeated calls.** A worker tool call with the same tool and the same canonical arguments, or a
delegate with the same role and the same task, that has already been repeated
`Orchestrator.Budget.MaxEquivalentCalls` times in a row with an unchanged result is refused before it
runs (with the default 2, the first call and two identical repeats run and the fourth call is
refused). The worker receives a tool failure with status `NotExecuted` and code
`repeated_call_without_new_evidence`; the orchestrator receives the same code as a delegate result.
The attempt is not failed and no reprompt is charged. "The same result" ignores the fresh artifact ids
a call writes, so two identical memory searches that find the same runbook are the same result, and a
result that changed resets the count. Once a call is refused it is not run again in that attempt, so
data behind that exact call is not re-read later. The check runs after the policy decision, so
`rate_cap` is unchanged. A worker that proposes two refused calls in a row is stopped without an
answer: the orchestrator receives `worker_stopped_repeating`, no worker output is stored and no schema
reprompt is charged.

**Progress.** An orchestrator turn makes progress when it adds evidence not seen before in the attempt
or changes the candidate classification. Time is not an input, so a slow model that keeps producing is
never a stalled one. When consecutive turns without progress go past
`Orchestrator.Budget.MaxTurnsWithoutProgress` (default 4, range 2 to 32):

- with a recovery left (`Orchestrator.Budget.MaxRecoveries`, default 1, range 0 to 3), the backend
  makes one diagnostic call on the orchestrator route with the new call kind `recovery`, through the
  same budgets, provider limits and `ModelCall` accounting. It is offered no tools and sees only a
  backend summary of counts, role and tool names, the evidence count, the candidate classification
  and the fixed task text. Its answer, bounded to 2000 characters, reaches the orchestrator as a user
  message marked as a suggestion, and the orchestrator continues through its own tools. The recovery
  call cannot start another recovery. A provider outage or another failure a later attempt could get
  past goes back to the job runner as for any call. A failure that would repeat for the same request
  has its accounting written and uses up that recovery; the attempt continues, with a fixed backend
  note telling the orchestrator to change its next call, only when another recovery and another
  window remain, and otherwise ends as below.
- with none left, when the remaining turns or workers could not hold another window, or when the token
  budget or context window leaves no room for the recovery call, the backend publishes its own report
  through the same grounding path: status `InsufficientEvidence`, classification `Unknown`, confidence
  `Low`, a fixed summary and next action, and a fixed limitation that names why it stopped. It cites
  only the trigger signal, and on a re-triage the recurrence state. The job succeeds, and the report's
  `ReportPublished` ledger rationale opens with `backend_authored: `, which a model-authored report
  cannot produce. Reserved backend text in a model-authored report is refused or removed.

Running out of turns or workers inside a stall the attempt detected, and that no progress has ended
since, also ends with the backend report; otherwise both limits dead-letter as before. A backend
report the repository refuses dead-letters as `triage_no_progress_termination_failed`.

`Orchestrator.RecoveryInstructions` optionally points at other recovery instructions; absent, the
built-in text shipped as `config/instructions/recovery.md` is used, and a configuration that does not
set any of the new keys keeps its hash. Every intervention writes a `BudgetEvent` with the
`no_progress:` prefix (`repeated_call`, `turns_without_progress`, `worker_stopped`, `recovery`,
`recovery_failed`, `terminated`) and a log event.

### Model answers that cannot be used are refused sooner

- A completion the provider cut off at its output ceiling is refused, on the JSON path and the streamed
  path alike. Any answer whose first choice reports `length`, `max_tokens` or `model_length` as its
  finish reason is `provider_output_limit_reached`, with or without partial content and with or without
  tool calls: the partial text is discarded and never reaches a caller, and a tool call whose arguments
  the ceiling cut through is reported under that code instead of as an invalid tool call. Those three
  are the whole list, and any other finish reason is not treated as a cut. It previously became a normal
  completion as soon as it carried any text or tool call, so half a report or half a remediation diff
  could be used as if it were the whole answer. The job dead-letters without a retry, as an output limit
  already did, and the operator's fix is a higher route `MaxOutputTokens` or a smaller task.
- `publish_report` accepts exactly one argument shape per call: `report_json` alone, `report` alone,
  or a bare report. It previously took the first wrapper it found. `summary` (4000 characters),
  `recommendedNextAction` (2000), `limitations` (20 items of 1000 characters), `evidence` (50 items)
  and `quote` (1000) are refused past their limit rather than truncated, with a reprompt that names
  the limit, and the tool description states the same limits. They are not schema keywords, because
  grammar-constrained local runtimes expand large length bounds into very large grammars.
- A role output schema that types a secret-named property as an object, number or boolean is refused
  when the configuration loads and in `config validate`, naming the role, the schema path and the rule
  that matched. Redaction replaces such a value with a string, so the output used to validate and then
  fail to parse on every attempt. A mismatch the load check cannot see, for example behind a `$ref`,
  now ends the delegate as a non-retryable invalid worker output.

### Other fixes

- A remediation pass whose model call the provider answered, but whose ledger accounting failed, was
  dead-lettered as `remediation_model_call_failed`, which was false. It now writes the owed row and
  dead-letters as `remediation_answer_unrecorded`: the call is paid for, the answer is discarded and no
  diff is kept. If that write fails too, it dead-letters as `remediation_model_call_accounting_pending`.
  Neither is retried, because a retry would pay for the model call again.
- The production API read memory health under a seed scope the Worker never wrote. Production Compose
  now gives both services the seed tenant and owner from one shared block.

### Repository

- The Dependabot lock-file workflow is split into a `restore` job with no permissions and no secret,
  and a `push` job that runs no `dotnet` command, validates every uploaded file against an allow-list
  of tracked `packages.lock.json` paths and pushes without force. No token is reachable from the job
  that runs restore.
- The `push` job uses a short-lived GitHub App installation token when the App secrets are set. The
  personal access token is a deprecated fallback, and the job summary names the credential that
  pushed. [Versioning and release flow](versioning.md) documents the setup.

## Upgrade notes

**The first Worker start downloads the embedding model.** A Worker starting on an empty
`embedding-models` volume downloads the model file, about 118 MB, and its tokenizer, about 123 MB in
total, over HTTPS from `huggingface.co`, unless the files were placed in the volume offline beforehand.
Its start waits for that for up to `IncidentCompass:Embeddings:LocalOnnx:InstallTimeoutSeconds`, 900
seconds by default. Plan for the Worker's memory to grow by roughly the model file's size plus 100 to
200 MB; that is a planning figure, not a measurement. `down --volumes` deletes the model volume, and
the backup script does not back it up because its contents are reproducible from the pinned source.

**An existing deployment that embedded through an OpenAI-compatible server must choose.** The Compose
default is now the local model. To keep the server, set `INCIDENTCOMPASS_EMBEDDINGS_PROVIDER` to
`OpenAICompatible`, `INCIDENTCOMPASS_EMBEDDINGS_PROVIDER_ID` to `local-oai` and
`INCIDENTCOMPASS_EMBEDDINGS_MODEL` to that server's model id. To move to the local model, set
`INCIDENTCOMPASS_EMBEDDINGS_MODEL` to `intfloat/multilingual-e5-small` (production Compose requires the
variable; only `docker-compose.yml` alone falls back to that value when it is unset), and run
`memory rebuild` on the Worker once the model is installed: until then the corpus was built under
another route, and `memory_search` cannot reach it.

**Changing the route model follows an order, and a job caught across it dead-letters.** A job is pinned
to the route model of the API that created it, and the Worker refuses an embedding call whose model is
not the one it has installed. The procedure in
[Single-host production runbook](single-host-production.md), "Local embedding model", therefore runs
every one-off command with `--no-deps`, so `docker compose run` does not recreate the running API at a
moment the procedure did not choose, and goes in this order: install the model, let the queue drain and
stop the Worker, change `INCIDENTCOMPASS_EMBEDDINGS_MODEL` and recreate the API alone, run
`memory rebuild`, recreate the Worker. Installing a model leaves the previous corpus retrievable, and
a job created after the API was recreated waits in the queue and completes on the new corpus. What
remains is a job
created before the API was recreated and not finished before the Worker restarted: its snapshot names
the previous model, so its `memory_search` call is refused, it spends its attempts and is dead-lettered
with `memory_embedding_model_mismatch`.

**Run `memory rebuild` to get section chunks.** A corpus seeded before migration 036 keeps its
whole-file chunks and reports current; it is re-chunked only by `memory rebuild`. Any later change to a
chunking setting also needs `memory rebuild` before it takes effect.

**Two configuration keys are deprecated.** Both keep working under the old name until a release that
states their removal, with a warning naming the new key, and setting both names is an error.

- `Orchestrator.Budget.MaxWallClockSeconds` is replaced by `MaxAttemptDurationSeconds`. A configuration
  file or stored snapshot that sets only the old key keeps its explicit value as the ceiling. Loading
  such a configuration file logs warning event 2701; rehydrating a stored snapshot for a queued job
  does not. Setting both keys is a load error, for a rehydrated snapshot as well as for the file.
  Rename the key to clear the warning, and consider raising a value chosen as a short total deadline
  before the provider call limits existed.
- Chat `IncidentCompass:ModelGateway:OpenAiCompatible:TimeoutSeconds` is replaced by
  `FirstOutputTimeoutSeconds`. When only the old key is set, its value becomes the first-output limit
  (warning event 2801 at host start). The warning and the both-set check run only when the chat
  provider is OpenAI-compatible. The old key no longer bounds reading the body, which the inactivity
  limit now does. The embedding section's `TimeoutSeconds` is not deprecated.

**A stalled investigation now ends with a report instead of running to the turn limit.** A job whose
orchestrator keeps making turns without new evidence gets one recovery call and then succeeds with a
backend-authored `InsufficientEvidence` report, where it previously ran until `MaxTurns` and
dead-lettered. Set `Orchestrator.Budget.MaxRecoveries` to 0 to skip the recovery call, or raise
`MaxTurnsWithoutProgress` to give a slow-converging model more room. A model that repeats the same call
with the same result is now refused after two repeats, where only `rate_cap` bounded repeats of a call
before.

**A cut-off model answer now fails the job instead of being used.** A completion the provider finished
at the output ceiling is refused whatever it carried, so a verbose model on a tight route ceiling
dead-letters where it previously published a half answer. Raise the route's `MaxOutputTokens` toward
what the model allows, or give the call a smaller task.

**`memory_search` answers a foreign-script query where it returned nothing.** On a host with no
relevance judge, items admitted that way are banded `low` and the result carries the `vector-only
matches, not lexically confirmed` message; dropping them is the memory role's job. Set
`Tools.memory_search.VectorOnlyFallback` to `off` to keep the previous empty result. The fallback does
not run on a host that has a judge, because there is no lexical gate left to leave anything empty.

**A deployment that runs no judge can no longer have a memory-based known-incident report at all.**
Nothing is confirmed without a judge, and the `KnownIncident` bar refuses a report whose memory
citations carry no confirmed band, so such a report is refused and costs a correction turn; the
backend can still publish under another classification. Memory still retrieves documents and passes
them on as context. The shipped compose files run the judge, the evaluation stack included, so this
reaches a deployment that removed or emptied the judge's model directory, or set
`Tools.memory_search.RelevanceJudge` to `off`.

**An upgrade that does not set the judge's model directory runs no judge, and still gets the rest of
this release's retrieval changes.** With the default `LocalOnnx` provider,
`IncidentCompass:RelevanceJudge:LocalOnnx:ModelDirectory` is the setting that turns a judge on. A
deployment that does not set it downloads nothing, starts normally and keeps the pre-judge admission
path: `memory_search` admits on lexical support alone, confirms nothing and says so in its new
top-level `limitation` string. What it does not keep is the behaviour that path had in 0.4.1. All of
the following reach it:

- Lexical coverage is judged per candidate and per script, so a word in a writing system a candidate
  never uses is no longer counted against it.
- `Tools.memory_search.VectorOnlyFallback` defaults to `foreign_script`, so a query in a script no
  candidate writes now returns unconfirmed items where it returned nothing, as the note above says.
- `retrievalConfidence` no longer derives from the vector score. On this host every item is `low`,
  and the numeric `score` is unchanged beside it.
- A `memory_search` query longer than 1016 characters is refused as `invalid_arguments`, whether or
  not a judge is there to read it.
- **The new `KnownIncident` publication bar applies, and on this host nothing meets it.** A
  `KnownIncident` report that cites a memory document is refused where it would have published, as
  the note above says; `VectorOnlyFallback` does not change that, because no setting confirms a
  document without a judge.
- **`memory model status` and `memory model install` now exit 1 where they used to exit 0**, because
  each command reports both models and takes the worse of the two results. That is the intended
  signal, and a deployment that means to run no judge is the one case to ignore it.

**A deployment that does set it downloads about 544 MiB on the first Worker start, and so does the
evaluation stack.** The judge's install pass runs after the memory seed pass, bounded by
`IncidentCompass:RelevanceJudge:LocalOnnx:InstallTimeoutSeconds`, 1800 seconds by default.
`docker-compose.yml` and `compose.production.yml` put the judge's directory inside the existing model
volume at `/app/models/relevance-judge`, and `compose.evaluation.yml` inherits it, so a real-model
evaluation on a fresh volume waits for the same download. No new volume is needed, `down --volumes`
deletes it with the rest and the backup script does not back it up because its contents are
reproducible from the pinned source. Plan for the Worker's memory to grow by roughly each model
file's size plus 100 to 200 MB, so by roughly 1 GiB once both are loaded; that is a planning figure,
not a measurement. The files can be placed offline instead, and the runbook gives that procedure.

**A judge that failed its install, or does not match, spends job attempts instead of pausing
claims.** While the configured judge is not usable because its install failed or timed out, its
files do not verify, or it is still installing, every `memory_search` call is refused with
`memory_relevance_judge_unavailable`, and with `memory_relevance_judge_mismatch` when the installed
judge is not the configured one. Both are configuration failures, as for the embedding model: the
attempt stores the code, spends the job's ordinary attempt budget and is dead-lettered under that code
when the budget runs out, and job claims are never paused. A judge that is installed but fails to
load is a provider outage instead, and is retried without spending attempts. On a first start on an
empty volume the Worker does not claim work until the judge's install pass has ended, so signals sent
during the download wait in the queue; if the pass fails, every one of them that reaches
`memory_search` then spends its attempts. After a first start, and after emptying the volume, check
that the judge installed before sending incidents: log event 2320 rather than 2321, or
`memory model status` exiting 0.

**A host that redacts `retrievalConfidence` or `confirmationScore` no longer loads.** Naming either
in `Redaction.AttributeKeys` or `Redaction.UserIdentifierAttributes`, in any casing, is refused when
the configuration loads and in `config validate`. Redaction replaces a matching property's value with
the marker whatever its kind, so the first key would turn every confirmed band into a value that
confirms nothing, on the durable artifact as well as in the tool result, and every `KnownIncident`
resting on memory would then be refused; the second would erase the score the band was decided from.
No shipped or sample configuration names either. Remove the key to load.

**A Worker whose judge provider is not one of the two provider kinds no longer starts.**
`IncidentCompass:RelevanceJudge:Provider` is new and defaults to `LocalOnnx`, so a host that does not
set it is unaffected. The value is parsed like the other provider settings, ignoring case, surrounding
space, hyphens and underscores, so `LocalOnnx` and `Mock` are accepted in any of those spellings and
anything else stops the Worker at start. The shipped production compose file pins `LocalOnnx` and the
production preflight refuses any other value, so a production host deployed through them cannot run
`Mock`; a host that bypasses the preflight with its own override file is not covered by either.

**Nothing durable has to be rebuilt in either direction.** Nothing the judge produces is stored, no
corpus, generation or chunk records which judge ran, and there is no re-embedding, no `memory rebuild`
and no route model to keep in step. Adding a judge to an existing corpus, or emptying the directory to
remove one, is reversible.

**The judge's three triage-configuration keys are optional, and a configuration that sets none of them
keeps its hash.** `Tools.memory_search.RelevanceJudge`, `RelevanceConfirmScore` and
`RelevanceFloorScore` are absent from every shipped and sample configuration, and the hash is taken
over the file as written, so adding this release's keys to a host without setting them leaves every
stored snapshot rehydrating unchanged. **The shipped configuration's own hash does move**, because the
shipped instruction files and the memory role's output schema changed: the role is told it may query
in the incident's own language and to search with the route too, the three top-level `message`
sentences are explained, both instructions say searching again cannot raise the band of a document
already shown, the orchestrator instruction states the `KnownIncident` bar, and
`retrievalConfidence` became a required property of a memory item rather than an optional one.
Queued jobs keep rehydrating the snapshot they were created under.

**Streaming is on by default.** Set `IncidentCompass:ModelGateway:OpenAiCompatible:Streaming` to
`false` for a provider that rejects `stream` or `stream_options`, or that streams tool calls in a shape
this adapter refuses.

**The Dependabot personal access token is deprecated.** Where `DEPENDABOT_LOCKFILE_TOKEN` is configured,
create the GitHub App described in [Versioning and release flow](versioning.md) and store
`DEPENDABOT_LOCKFILE_APP_ID` and `DEPENDABOT_LOCKFILE_APP_PRIVATE_KEY` as Dependabot secrets.

## Defaults and compatibility

- The API remains under `/api/v1`, and no HTTP property was added, removed or renamed. The `state` of
  `GET /api/v1/health/memory-corpus` can now also read `EmbeddingModelMismatch`,
  `EmbeddingModelUnavailable` or `ChunkPolicyChanged`; `RebuildRequired` is false for the two model
  states, which a rebuild cannot repair.
- One migration was added, `036-memory-chunk-structure.sql`, catalog version 27. It adds two nullable
  columns and rewrites nothing, so a 0.4.1 database upgrades in place.
- New error codes: the three phase timeouts above, `remediation_answer_unrecorded`,
  `memory_embedding_model_mismatch`, `memory_embedding_model_unavailable`,
  `memory_chunk_policy_changed` and the local model install codes listed in
  [Model gateway](model-gateway.md), `tool_execution_timeout`, `tool_execution_failed`,
  `dispatch_not_invoked`, `repeated_call_without_new_evidence`, `worker_stopped_repeating` and
  `triage_no_progress_termination_failed`. The relevance judge adds
  `memory_relevance_judge_unavailable`, `memory_relevance_judge_mismatch`,
  `memory_relevance_judge_score_not_finite` and `memory_relevance_judge_score_count_mismatch`, over
  the adapter's own fifteen `relevance_judge_...` absence, install, store and runtime codes, which
  [Model gateway](model-gateway.md) lists in full. New log
  events: 2701 and 2801 for the deprecated keys, 3213 for a skipped fallback whose budget event could
  not be recorded, 3305 to 3309 and 3521 for tool execution limits, 3404 to 3411 for repetition,
  progress, recovery and termination, 2320 to 2322 for the judge's install state, including the
  once-per-start note on a host that configures no judge, and 2323, the warning at every start of a
  Worker that runs the mock judge.
- New optional configuration keys: `Tools.<id>.TimeoutSeconds`, `Tools.memory_search.VectorOnlyFallback`,
  `Orchestrator.Budget.MaxEquivalentCalls`, `MaxTurnsWithoutProgress`, `MaxRecoveries`,
  `Orchestrator.RecoveryInstructions` and the judge's `Tools.memory_search.RelevanceJudge`,
  `RelevanceConfirmScore` and `RelevanceFloorScore`. The shipped configuration sets none of them, so
  none of them moves a configuration hash by being added. `ModelCall` rows
  gain the call kind `recovery`, and tool-call telemetry gains the outcome `refused`.
- The relevance judge is a host setting, not a provider or a route.
  `IncidentCompass:RelevanceJudge` is new: `Provider` takes `LocalOnnx`, the default, or `Mock`, and
  with the local judge only `LocalOnnx:ModelDirectory` has to be set; a blank or absent value is a
  host that runs no judge. No triage-configuration provider entry names the judge, nothing in the
  model gateway dispatches to it, it writes no `ModelCall` row and no health endpoint reports it. Its
  cost is Worker CPU, memory and disk.
- `memory_search` keeps its existing output shape and adds three nullable values. Each item gains
  `judgeScore`, the judge's admission score against the model's query, and `confirmationScore`, its
  score against the backend's query built from the trigger signal, both beside the unchanged vector
  `score` and each null when nothing judged it. The result gains a top-level `limitation`, non-null
  whenever no judge ran: on a host that runs none, with `RelevanceJudge` set to `off`, and when the
  judge became absent before the confirmation call. One judged case is all `low` without a
  limitation: a signal whose backend-built query has no counted word leaves nothing to confirm
  against, so the judge is not asked a second time. Two values change meaning. Each item's
  `retrievalConfidence` still reads `high`, `medium` or `low` and no longer derives from the vector
  score, because on the shipped embedding model relevant and unrelated chunks score alike. It is
  decided against the backend's query: `high` is the judge confirming the document with every counted
  word of that query also in it, `medium` the judge's confirmation alone and `low` admitted without
  being confirmed, and on an unjudged call every item is `low`. The top-level `message` gains a fourth
  value, `related matches, none confirmed by the relevance judge`, beside `matches found`,
  `vector-only matches, not lexically confirmed` and `no matches`, and `matches found` now means at
  least one item is confirmed. `matched`, `items` and `noMatchReason` are unchanged.
- `memory_search` refuses a query longer than 1016 characters as `invalid_arguments`, derived from
  the judge's 512-token window. The memory role's output schema now requires `retrievalConfidence` on
  every item it returns, where it was optional.
- Report publication gains one bar. A `Completed` report classified `KnownIncident` that cites at
  least one memory-backed retrieved document is refused unless at least one of them carries a
  confirmed band. Every other classification, a report citing no retrieved document, and a report
  grounded on a ticket-search or source-lookup `RetrievedItem` are untouched. A memory payload
  carrying no band does not count as confirmed.
- Configuration validation is stricter in three places: a role output schema with a secret-named
  non-string property, a configuration that sets both names of a deprecated key, and
  `retrievalConfidence` or `confirmationScore` named as a redaction attribute key. None of the three
  affects the shipped configuration, but the third can fail the load of a host that configured it;
  see the upgrade notes.
- An embedding call still writes no `ModelCall` row, whichever adapter serves it. The local model has
  no provider to bill; its cost is Worker CPU and memory.
- Automated tests do not call a real model or embedding provider. The local model is exercised against
  a tiny generated fixture model; one integration test runs the real pinned model, downloading it when
  its cache is empty, where the Docker-backed tier is required (CI or
  `INCIDENTCOMPASS_REQUIRE_DOCKER_TESTS`). The judge is exercised the same way, against a tiny
  generated fixture cross-encoder in the deterministic suites and against the real pinned judge only
  in opt-in legs that install it. The mock judge is what the mock stack runs; it is a deterministic
  stand-in, not a measurement of relevance.

## Not in this release

- Semantic progress detection. Repetition and progress are judged from result identities and the
  candidate classification, so a model that loops through reworded tasks or calls is not caught as
  repeating.
- Cross-language retrieval on a host that runs no relevance judge. There, the lexical rules are still
  the whole answer: another script works by default, because a word that cannot occur in the corpus is
  no longer counted against a candidate and the vector-only fallback covers what is left, but a
  Latin-script language over a Latin-script corpus, such as Polish over English runbooks, still finds
  nothing unless an operator sets `Tools.memory_search.VectorOnlyFallback` to `always`, which also
  removes the empty result for every other query without lexical support, and whatever it returns
  there is context only, because nothing is confirmed without a judge. A host that runs the judge
  answers all three measured languages without that setting.
- Telling a truthful signal from a crafted one. A confirmed band records that the relevance judge
  found the document to describe the incident as its trigger signal describes it, and the confirming
  query is that signal. A signal that itself paraphrases a runbook is therefore judged to describe
  it: an error message crafted from a runbook's wording earns a confirmation from untrusted telemetry,
  and nothing in retrieval can tell such a signal from a truthful one. A user or manual report
  confirms against the reporter's own summary, so a memory-based `KnownIncident` resting on one is
  only as trustworthy as the reporter. The publication rule bounds what an unconfirmed document may
  justify; it does not prove that a confirmed one describes this failure. The control is the approval
  outward actions already wait for: a ticket, a ticket update, a code write applied through
  `remediation_apply`, a branch push and a pull request each wait for a person, and a notification is
  the only action category policy may approve on its own. The one exception is the remediation diff
  pass, `remediation_diff` in category `code_write`, which runs without approval once an operator
  enables it; it spends model budget and writes only a disposable copy of the checkout, and its diff
  can leave the host only through that approved apply. What this release does close is the model raising a band by searching again with a
  document's wording.
- Accounting of external embedding calls. An embedding call served by an OpenAI-compatible server
  still appears in no count, token total or spend figure.
- Requeueing a job across a change of the embedding route model. A job created before the route model
  changed and still unfinished when the Worker restarts dead-letters with
  `memory_embedding_model_mismatch`; the documented order keeps that to jobs already in flight, see the
  upgrade notes.

## Verification

The release gate runs locked-mode restore, build with zero warnings and zero errors, formatting
verification, the code-organization gate, the package-vulnerability gate and the internal-reference
gate.

`dotnet test --solution IncidentCompass.slnx` runs the deterministic unit and integration suites,
including the PostgreSQL-backed integration tests through Testcontainers with
`INCIDENTCOMPASS_REQUIRE_DOCKER_TESTS` set.

On the final release tree the full solution run reported 3200 tests: 3195 passed, 0 failed and 5
skipped.
The skips are three symbolic-link tests the Windows test process cannot create links for, the explicit
OpenAPI baseline regeneration, which only `scripts/update-openapi-baseline.ps1` runs, and the real
relevance judge test, which is skipped unless `INCIDENTCOMPASS_REQUIRE_REAL_RELEVANCE_JUDGE` is set.
The judge benchmark is gated too, by `INCIDENTCOMPASS_RELEVANCE_JUDGE_BENCHMARK`, but it returns
without doing anything rather than reporting as skipped, so it is not one of the five. Neither judge
leg runs merely because `CI` is set: the ONNX file is about 544 MiB, and fetching it on every run buys
less than it costs.

The suites were also run on Linux in the .NET 10 SDK container, since CI and the release workflow
run on Linux. There, every test passed except those that drive the Docker CLI and Compose against the
host daemon from inside the container, which that setup cannot serve; they run in CI. A fresh-host Compose check
exercised the shipped defaults, and a scripted streaming provider check exercised the streamed chat
path.

The judge was verified on a fresh host on the final tree: empty volumes, shipped defaults and the mock
chat provider. Both models install on the first Worker start with no operator action, and
`memory model status` exits 0 reporting both, with the judge's pinned revision, its Apache-2.0 license
and both digests. The model volume ends up holding the two models in separate directories with no
collision between them, at the sizes [Model gateway](model-gateway.md) records: about 123 MB for the
whole embedding model directory and about 549 MiB for the whole judge directory, each figure covering
that directory's manifests and both of its artifact files.

Eight signals were probed against the English seed corpus. English, Polish with full diacritics,
Polish without diacritics, and Russian with Latin identifiers each reached a completed known-incident
report, confirmed against the backend's query from the signal. The Polish signal without diacritics
is the case the route was added for: it was confirmed at 1.95 against the shipped confirm score of
0.85, where a live run of the same signal without the route in the confirming query had scored 0.47,
below the confirm score, and come out as insufficient evidence. An all-Cyrillic Russian signal, and
off-topic signals in English, Polish and Russian, each returned no matches.

The threshold, attack and latency figures in this release come from the opt-in benchmark leg that
runs the real `memory_search` over the grown corpus, not from an offline sweep. At the shipped 0.85
the attack that replaces the model's query with the text of the closest document confirms 0 of 12 in
each of English, Polish and Russian, where confirming against the model's query confirmed 12 of 12 in
each. No hard negative is confirmed in any language, every incident-shaped positive is confirmed in
all three, English off-topic false positives are 0 and no English positive query goes unanswered. An
English hard negative is confirmed at a confirm score of 0.55 and below, and every incident-shaped
positive holds up to 1.10; 0.85 is the middle of that window.

The documented embedding-model change procedure was re-run on a fresh host as well, because this
release generalized the model store and moved the manifest to schema 2. That run predates the change
that moved confirmation to the backend's query, which touches neither the embedding model nor its
store, and it was not repeated after it. The result is identical to the
record made when that procedure was first verified: the corpus rebuilds onto the new embedding model,
a report published after the change cites its documents again, and the one probe created by the old
API while the Worker was stopped still dead-letters, which is the documented residual of that window.
**Changing the embedding model does not affect the judge and needs no judge action.** The judge was
untouched throughout, and `memory model status` exited 0 on both models at the end.

What that does not cover:

- No automated test reaches a real chat provider or an external embedding server. No real chat model
  ran anywhere in this verification, the fresh-host check included, which used the mock chat provider.
  The real local embedding model runs in one integration test in the Docker-backed tier and in the
  opt-in benchmark; the benchmark is not part of any gate.
- The judge's real artifact is exercised by the fresh-host check and by the two opt-in legs above, and
  by nothing CI runs. Everything the adapter does with a model is covered deterministically against a
  committed fixture cross-encoder, which says nothing about the pinned model's own scores. The
  thresholds therefore rest on one measured run of the real tool rather than on a gate.
- The measured retrieval figures are three languages over one 24-item corpus of shipped-style
  runbooks. They are a measurement of this corpus, not of the judge in general, and a corpus whose
  answers are not duplicated across chunks would pay the floor's cost as a lost answer rather than as
  a lost duplicate.
- The attack figures come from the benchmark substituting the document's text for the query, not from
  a model attempting it, since no real chat model ran.
- An incident carrying no Latin identifier at all was not answered end to end. The all-Cyrillic
  Russian probe returned no matches, where the same incident with its service name and error type
  left intact reached a confirmed known-incident report. The honest
  empty result is the designed outcome of finding nothing, not a claim that nothing was there.
- The Docker-backed integration coverage is enforced by an opt-in environment variable rather than by
  default.
- The streaming checks use scripted providers. Behavior against a specific provider's stream is
  covered only by the incompatibilities documented in [Model gateway](model-gateway.md).
- Tool execution limits, repetition detection, bounded recovery and honest termination are exercised
  with scripted model clients and a manual time provider, not against a real model.
