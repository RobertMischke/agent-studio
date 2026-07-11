# Token pricing

> **Status (2026-07-11):** Live. Pricing is owned by the
> `CodingAgentRunner` package. Studio contains no model rates.

## Contract

Every Studio cost calculation goes through
`backend/Features/Runner/TokenPricing.cs`. This is a compatibility adapter over
`CodingAgentRunner.Pricing.ModelPriceCatalog.Default` and its CAR-3 historical
pricing API.

Callers must supply the run or event timestamp. CAR selects the catalog entry
whose `ValidFrom` applies at that time. Repricing old usage with today's rate is
not allowed. Model metadata in `CliModels.cs` exposes current rates only as a
catalog pass-through for discovery consumers; it owns no price numbers.

CAR distinguishes `Resolved`, `UnknownModel`, and `NoPriceForDate`. Studio maps
only `Resolved` to a dollar value. The other states keep the token count and set
the existing unknown-price flags so UI surfaces render `Unknown`, never a
silent `$0.00`.

## Cost surfaces

The shared adapter feeds:

- task-detail pipeline step, run, model, and total costs;
- project pipeline cost columns and time series;
- project expensive-jobs, heatmap, and drill-down aggregates;
- workspace token timeline and Token Summary;
- CLI Usage modal and detail table, including "Cost per model";
- ad-hoc/supporting usage summaries.

These surfaces may aggregate resolved costs, but an aggregate containing an
unpriced call remains explicitly marked unknown or partial according to its
wire contract. A per-model row with a missing price always renders `Unknown`.

## Verification

`backend.Tests/TokenPricingTests.cs` pins the adapter to the CAR catalog,
including alias normalization, unknown-model behavior, no-price behavior, and
selection by run timestamp. Aggregator tests cover the shared consumers.
