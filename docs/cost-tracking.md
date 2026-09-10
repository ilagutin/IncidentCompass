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
- effective dates.

Rows are operator-maintained database configuration in this reference implementation. There is no
public price administration or reload API, and currencies are never converted or combined.

## Quotas

Cost quotas are not implemented. This rollup is a read model over recorded usage; nothing in the
system refuses work because of cost. Two nearby mechanisms are often mistaken for quotas and are
not: the per-attempt orchestrator budget bounds one investigation's tokens, workers and wall clock,
and the API rate limiter bounds requests per authenticated key. Neither tracks spend, and neither
accumulates across investigations or across a billing period. A real quota would need its own
governed policy and its own enforcement point rather than a threshold read off this rollup.
