# Cost Tracking

Model-backed features are cost-sensitive. The live system records token usage in triage-ledger `ModelCall` events and charges attempt budgets with first-class `BudgetEvent.tokens_delta` rows.

## Live Inputs

- input tokens;
- output tokens;
- total tokens;
- usage source (`provider`, `estimate` or `unknown`);
- provider;
- model;
- route ID;
- request count derived from `ModelCall` rows.

## Hourly Rollup

`GET /api/v1/observability/cost-rollups` exposes one authenticated tenant-scoped read model. It uses:

- `ModelCall` rows in `incidentcompass.triage_ledger` for usage, provider and model;
- `incidentcompass.ai_model_pricing` for effective-dated pricing.

The required UTC window uses an inclusive start, exclusive end and a maximum duration of 31 days.
Each returned UTC-hour bucket contains model-call, priced-call and unpriced-call counts, valid token
totals, and exact spend totals separated by currency. The response is aggregate-only: tenant, fault,
job, provider, model and route identifiers are not exposed.

Malformed ModelCall metadata and ambiguous pricing fail closed to unpriced. A valid call without a
price still contributes tokens; invalid or overflow token metadata contributes no token or spend
value. Matching is case-sensitive and requires exactly one interval at the call timestamp. The system
does not write estimated cost to a separate request-log table and does not expose a usage dashboard.
The mock models retain zero-cost USD pricing records so deterministic local calls are explicitly
priced rather than confused with missing pricing.

`callCount` counts every durable `ModelCall` row, whether the call succeeded or failed. When an
unsuccessful call has unknown usage, its input, output and total token fields are null. That row still
increments `callCount` and `unpricedCallCount`, but it contributes no tokens and no spend. The rollup
does not turn missing usage into a zero-token priced call or fabricate an estimate.

## Pricing Table

`incidentcompass.ai_model_pricing` records include:

- provider;
- model;
- currency;
- input token price;
- output token price;
- embedding token price where applicable;
- effective dates.

Rows are operator-maintained database configuration in this reference implementation. There is no
public price administration or reload API, and currencies are never converted or combined.

## Quotas

Future quota examples:

- max requests per user per day;
- max tokens per user per day;
- max estimated cost per user per month;
- max requests per tenant per day, optional.
