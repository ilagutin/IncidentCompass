# Cost Tracking

Model-backed features are cost-sensitive. The live system records token usage in triage-ledger `ModelCall` events and charges attempt budgets with first-class `BudgetEvent.tokens_delta` rows.

## Live Inputs

- input tokens;
- output tokens;
- total tokens;
- usage source (`provider`, `estimate` or `unknown`);
- provider, the adapter that answered;
- provider ID, the configured provider entry the called route named;
- model;
- route ID;
- request count derived from `ModelCall` rows.

## Provider Versus Provider ID

These are two different facts and the rollup depends on the difference. `provider` is the adapter
that produced the answer, and one adapter answers for every endpoint of its kind the host can reach:
every OpenAI-compatible provider records the same `openai-compatible` string. `providerId` is the
entry in the triage configuration's `Providers` table that the called route named, and that entry is
what has its own endpoint, its own credential and its own prices.

`incidentcompass.ai_model_pricing` is keyed on `(provider, model)`, and the value matched against
that column is the call's **`providerId`**. Two configured providers reached by one adapter are two
payers with two price lists, and pricing them under the adapter name would add their spend together
under whichever price happened to match. Nothing in the pricing table's schema changed for this; what
changed is which recorded value is looked up in it.

## Hourly Rollup

`GET /api/v1/observability/cost-rollups` exposes one authenticated tenant-scoped read model. It uses:

- `ModelCall` rows in `incidentcompass.triage_ledger` for usage, provider and model;
- `incidentcompass.ai_model_pricing` for effective-dated pricing.

The required UTC window uses an inclusive start, exclusive end and a maximum duration of 31 days.
Each returned UTC-hour bucket contains model-call, priced-call, unpriced-call and estimated-usage
call counts, valid token totals, the token total that came from estimated usage, and exact spend
totals separated by currency. The response is aggregate-only: tenant, fault, job, provider, model and
route identifiers are not exposed.

A call produces spend only when both of these hold, and both are about what the row says rather than
about the price table:

- the row names the configured provider that answered, and exactly one case-sensitive
  `(providerId, model)` pricing interval contains the call timestamp;
- the row's `usageSource` is `provider`, meaning the token counts are the ones the provider itself
  reported.

Everything else is unpriced. Malformed `ModelCall` metadata and ambiguous pricing fail closed to
unpriced. A valid call without a price still contributes tokens; invalid or overflow token metadata
contributes no token or spend value. The system does not write estimated cost to a separate
request-log table and does not expose a usage dashboard.

`callCount` counts every durable `ModelCall` row, whether the call succeeded or failed. When an
unsuccessful call has unknown usage, its input, output and total token fields are null. That row still
increments `callCount` and `unpricedCallCount`, but it contributes no tokens and no spend. The rollup
does not turn missing usage into a zero-token priced call or fabricate an estimate.

### Estimated Usage Is Never Priced

When a provider returns no usable token counts, the call is still charged against the attempt budget
using this system's own estimate, and the ledger row records `usageSource: "estimate"` to say so. The
rollup counts that call and its tokens and never prices it, even when a matching price exists.

The reason is what a spend figure is for. An operator compares it against a provider invoice, and an
invoice is built from the provider's own counts; a figure that silently mixed measured tokens with a
local approximation would differ from the invoice by an unknown amount with nothing in the response
to attribute the difference to. `estimatedUsageCallCount` and `estimatedUsageTotalTokens` are that
attribution: they are subsets of `callCount` and `totalTokens`, every estimated-usage call is also
counted in `unpricedCallCount`, and together they turn "spend looks low for this hour" into an
answerable question.

### What An Operator Can And Cannot Conclude

A spend figure is a lower bound on what the tenant was billed for the window, denominated per
currency, built only from calls that named their configured provider, reported their own token counts
and matched exactly one price. It is not the invoice: this system holds no discounts, minimums,
cached-token rates, batch rates or embedding charges, and it never converts or combines currencies.

`unpricedCallCount` above zero means the hour contains work that is not in the spend figure. It does
not say why on its own. `estimatedUsageCallCount` separates out one reason. The rest are a missing or
ambiguous price row, unreadable metadata, and a row that does not name its configured provider.

### Rows Written Before The Provider ID Was Recorded

A `ModelCall` row written before the configured provider was recorded names an adapter and nothing
else about who answered. Those rows still parse, still count, and still contribute their tokens, and
they are never priced: an adapter name is shared by every provider behind it, so treating it as a
payer would be a guess. Ledger rows are not rewritten, so this is permanent for those rows; it
affects a bounded window of history and not any call recorded after the change.

The same applies to the `mock` pricing records seeded with the schema. They are matched by a
configuration whose provider entry is itself named `mock`, not by the mock adapter answering for a
provider named something else, so a mock-mode run against an ordinary configuration reads as
unpriced rather than as zero-cost.

## Pricing Table

`incidentcompass.ai_model_pricing` records include:

- provider, matched against a call's configured provider ID;
- model;
- currency;
- input token price;
- output token price;
- embedding token price where applicable;
- effective dates;
- who last changed the row, when, and optionally why.

Rows are operator-maintained database configuration in this reference implementation. There is no
public price administration or reload API, and currencies are never converted or combined. The
rollup re-reads the table on every request, so an edit takes effect immediately, with no restart and
no reload step.

### Editing A Price Is Editing History

The table has no tenant column: a price is host-global, while every API identity here is
tenant-scoped. That is why there is no price endpoint. Adding one would need a cross-tenant admin
identity that this codebase does not have, and there is no admin or role concept anywhere in `src/`
to build it from. So the writer is an operator at a `psql` prompt.

An edit made today changes the answer the rollup gives for a window that closed last month, because
spend is recomputed from ledger rows and the current price table every time it is asked for. There
is no stored spend figure to be inconsistent with; there is only the figure someone already read and
wrote down. Schema version 21 constrains the hand-edit accordingly.

**A change names an author and a moment.** `administered_by` is operator-asserted free text and a
write without it is refused. It is deliberately not called an actor or a principal: nothing
authenticates it, and a column claiming an authenticated identity would be worth less than an honest
label. `administered_at_utc` is the opposite - the database stamps it on every insert and update and
discards whatever the statement supplied, so the timestamp cannot be quietly backdated.
`administration_note` is optional and is where the reason for a correction belongs. These columns
record the latest change, not a history of changes: a second correction overwrites the first one's
attribution, which is why the procedure below prefers closing an interval to editing one. Rows
written before version 21 keep NULL, because their author is not something this schema can name. The
five prices seeded by the schema are backfilled to name the schema, because theirs is.

**Two prices cannot cover one instant.** An exclusion constraint refuses an interval that overlaps
an existing one for the same provider and model. The rollup already treats that ambiguity as
unpriced and still does, which is the right behaviour at read time and remains the fallback for a
database restored from a dump taken before version 21. But silently dropping the spend is a poor way
to learn about a mistyped date: the operator who made it sees only a number that is too low. The
constraint moves the failure to the moment of the mistake. Intervals are half-open, so an interval
ending exactly where the next begins does not overlap, which is the normal shape of a price change.

**A price cannot be deleted.** The database refuses `DELETE`, the way it refuses changes to
published reports and action approvals. A deleted price is the one edit whose damage cannot be seen
afterwards: the row is gone, the hours it priced quietly become unpriced, and a figure already
reported to someone drops with nothing left to explain why. Retirement has a non-destructive form -
set `effective_to_utc` - which stops the price applying after that instant and leaves every hour
before it priced exactly as it was reported. Updates stay allowed, because correcting a genuinely
wrong price is legitimate and now leaves a name, a time and a reason behind it.

`docs/single-host-production.md` has the statements for adding, correcting and retiring a price, and
says what each does to figures already reported.

## Quotas

Cost quotas are not implemented. This rollup is a read model over recorded usage; nothing in the
system refuses work because of cost. Two nearby mechanisms are often mistaken for quotas and are
not: the per-attempt orchestrator budget bounds one investigation's tokens, workers and wall clock,
and the API rate limiter bounds requests per authenticated key. Neither tracks spend, and neither
accumulates across investigations or across a billing period. A real quota would need its own
governed policy and its own enforcement point rather than a threshold read off this rollup.

## Cost Alerts Are Not Built

Durable cost alerts - a background evaluator that compares this rollup against a threshold and
delivers a notification someone acknowledges - are deliberately not implemented. Three of the
concepts such a feature needs have no coherent definition in this system, and the fourth is better
served outside it.

**Nobody owns the alert.** The evaluator would be a background pass, and the background identity
here has no tenant at all. Prices are host-global while every read of this rollup is tenant-scoped,
so a threshold crossed against host-wide prices is not a fact about any one tenant, and delivering
it to a tenant would alert that tenant about numbers it cannot see for itself. The obvious fix is to
address the alert to an operator instead, but there is no cross-tenant operator principal to address
it to: the action operator identity in this system is derived from a tenant-scoped API key, and any
valid key is the minimal operator for its own tenant and nothing wider.

**Acknowledgement would mean nothing.** The acknowledgement this system already has is load-bearing
because it gates something: an action approval decides whether a dispatch happens, and until someone
decides, nothing is sent. Acknowledging a cost alert would gate no dispatch, suppress no effect and
release no budget. It would be a read receipt wearing the vocabulary of a governance decision, and
reusing that vocabulary for something with no consequence attached devalues it where it does have
one.

**Delivery would cost more than the feature.** The one delivery path in this repository runs through
the action approval outbox, and `action_approvals.origin_report_id` is `NOT NULL` with a foreign key
to a published report. A threshold breach has no report: it is an aggregate over a window, not a
conclusion about an incident. Making it fit would mean relaxing that column on the most
trigger-guarded table in the schema, which weakens the provenance requirement for every action that
already flows through it - a real loss of action policy in exchange for a notification.

**And an operator's own alerting does this better.** `GET /api/v1/observability/cost-rollups` is a
plain authenticated read. An alerting rule in whatever the operator already runs can poll it, keep
its own thresholds and history, route to whoever should be woken, and deduplicate and silence the
way that tool already knows how to - none of which an in-process evaluator here would do as well.
This project describes itself as a consumer of an observability pipeline rather than an
observability backend, and cost alerting is exactly the responsibility that framing puts on the
other side of the boundary.

What this leaves out on purpose: threshold configuration, an alert evaluator, an acknowledgement
lifecycle, spend quotas, a usage dashboard and cost exports.
