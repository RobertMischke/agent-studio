# Token pricing

> **Status (2026-07-21):** Live. Pricing is owned by the published
> `TokenEconomy` package. Studio contains no model rates.

## Contract

Every Studio cost calculation goes through
`backend/Features/Runner/TokenPricing.cs`. The package-specific implementation
in `backend/Features/Runner/TokenEconomyPriceProvider.cs` adapts
`TokenEconomy.ModelPriceCatalog.Default` and its historical pricing API.

The adapter is exposed through `ITokenPriceProvider`; the active
`TokenPricing.Provider` configuration selects `TokenEconomyPriceProvider`, the
package-specific implementation from the exactly pinned `TokenEconomy` 0.2.0
dependency. Aggregators and frontend contracts do not depend on the provider
package directly.

Callers must supply the run or event timestamp. TokenEconomy selects the catalog entry
whose `ValidFrom` applies at that time. Repricing old usage with today's rate is
not allowed. Model metadata in `CliModels.cs` exposes current rates only as a
catalog pass-through for discovery consumers; it owns no price numbers.

TokenEconomy distinguishes `Resolved`, `UnknownModel`, and `NoPriceForDate`.
Studio maps only `Resolved` to a dollar value. The other states keep the token
count and set the existing unknown-price flags so UI surfaces render `Unknown`,
never a silent `$0.00`.

## Cost surfaces

The shared adapter feeds:

- task-detail pipeline step, run, model, and total costs;
- code-review and quality-grade rows from their generated-file usage
  provenance;
- project pipeline cost columns and time series;
- project expensive-jobs, heatmap, and drill-down aggregates;
- workspace token timeline and Token Summary;
- CLI Usage modal and detail table, including "Cost per model";
- board task and project token badges;
- ad-hoc/supporting usage summaries.

These surfaces may aggregate resolved costs, but an aggregate containing an
unpriced call remains explicitly marked unknown or partial according to its
wire contract. A per-model row with a missing price always renders `Unknown`.

## Calculation transparency

`POST /api/token-pricing/calculate` accepts up to 100 model/token/timestamp
items and returns the exact historical catalog entry used for each item:
input, output, cache-read, and cache-write rates per million tokens, component
costs, currency, price source, and effective date. The shared frontend cost
breakdown dialog is the only renderer for this contract. Cost launchers pass
the recorded run timestamp where one exists so the dialog and aggregate use
the same historical period.

The dialog is mounted once at app level and opened through
`CostBreakdownService` / `CostBreakdownTriggerDirective`. New theoretical-cost
surfaces must use that shared path instead of adding a local modal or a price
table.

Compact token displays use `buildTokenCostTooltip` from the frontend tokens
feature. The helper owns USD formatting, the mandatory estimate caveat, partial
aggregate wording, and the explicit `no price data` fallback. Review rows,
task-detail pipeline totals, project Pipeline `Tokens / 90d`, and board badges
must use this helper instead of composing local tooltip strings.

## Verification

`backend.Tests/TokenPricingTests.cs` pins the provider adapter to the
TokenEconomy catalog, including alias normalization, unknown-model behavior,
no-price behavior, selection by run timestamp, and the configured provider
identity. The adapter also preserves the four effective rates, source, and
effective date returned to the shared calculation modal. Aggregator tests cover
the shared consumers.
