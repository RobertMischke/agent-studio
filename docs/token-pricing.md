# Token pricing — the single per-model price table

> **Status (2026-06-02):** Live. One price table, one source of truth:
> [`backend/Services/Runner/TokenPricing.cs`](../backend/Services/Runner/TokenPricing.cs).
> Every cost number the product shows (per-step Overview rows, the task
> total, and the project-level "Pipeline cost by step kind" trend) is
> derived from this table. Nothing computes a dollar figure on its own.

## Why a single table

Cost is shown for transparency, not billing. Agent Software Studio runs
its agents through CLI subscriptions (Pro / Max / Team / Enterprise), so
the real dollar cost on top of those plans is zero. The estimate exists
so a user can sanity-check whether the orchestrator is burning a
reasonable amount of capacity and compare models against each other.

Because the number is informational, the one rule that matters is
*consistency*: the per-step row, the task total, and the project trend
must all price the same tokens the same way. A single table guarantees
that. When you change a rate, you change it in one place and the unit
tests in `TokenPricingTests` re-pin the math.

## The table (USD per million tokens)

Current as of May 2026 from anthropic.com:

| Model id | Input / M | Output / M |
|---|---|---|
| `claude-opus-4-8` | $5.00 | $25.00 |
| `claude-opus-4-7` | $5.00 | $25.00 |
| `claude-opus-4-6` | $5.00 | $25.00 |
| `claude-opus-4-5` | $5.00 | $25.00 |
| `claude-sonnet-4-6` | $3.00 | $15.00 |
| `claude-sonnet-4-5` | $3.00 | $15.00 |
| `claude-haiku-4-5` | $1.00 | $5.00 |

### Cache pricing

Cache rates follow Anthropic's published policy and are derived from the
base input rate, so they move automatically when an input rate changes:

- **Cache read:** 10% of base input (`InputPerMillion * 0.10`).
- **Cache write (5-minute):** 125% of base input (`InputPerMillion * 1.25`).

The orchestrator's short, single-turn calls only ever take 5-minute cache
writes, so the 1-hour (2x) write rate is intentionally not modelled.

### Models deliberately excluded

Codex / Copilot / Gemini are **not** in the table. Those CLIs spend the
user's OpenAI / GitHub / Google subscriptions under their own pricing
models, which are not directly comparable to Anthropic's per-token rates.
For an unlisted model `TokenPricing.Estimate` returns a zero-cost estimate
with `ModelKnown = false`; consumers render the token counts but suppress
or asterisk the dollar figure rather than show a misleading `$0.00`. In
the project pipeline-cost trend this surfaces as a `*` next to a step
kind's cost and an `anyModelUnknown` flag, meaning "cost is a lower bound;
some steps used a model with no price on file".

## How cost flows from the table to the UI

The table is consumed at two granularities; both call the same
`TokenPricing.Estimate(model, in, out, cacheRead, cacheWrite)`:

1. **Per task (Overview).**
   [`PipelineCostCalculator.Summarize`](../backend/Services/Pipeline/PipelineCostCalculator.cs)
   reads the task's `pipeline-execution.json`, prices each recorded step
   (`PipelineStepCost`), and sums them into a `PipelineCostSummary` task
   total. The Overview pane shows one `<step> · <model> · <tokens> · <cost>`
   row per step plus the task total. This runs on the already-recorded
   record only; it does no scan and no network call, so it is cheap enough
   to recompute on every Overview poll.
2. **Per project over time (Token Usage).**
   [`ProjectPipelineCostService`](../backend/Services/Pipeline/ProjectPipelineCostService.cs)
   folds every task's `pipeline-execution.json` in the window into a
   per-day, per-step-kind series (`core` / `aspect` / `tool` /
   `orchestrator` / `module`), prices each via the same table, and serves
   it from `GET /api/projects/{project}/token-usage/pipeline-cost`. The
   result is cached (30s TTL, keyed by `watchPath|days`) so a poll does not
   re-walk the folder set — see the "aggregate incrementally" note in the
   task prompt and the O(N^2) bus-load incident it refers to. The Token
   Usage panel renders this as the "Pipeline cost by step kind" legend +
   stacked per-day trend.

## Maintenance

When Anthropic changes a rate, or a new model id ships:

1. Edit the `Catalog` dictionary in `TokenPricing.cs` (and the table
   above so the doc stays honest).
2. Run `dotnet test --filter TokenPricing` — the pinned-math tests will
   fail until the expected values match the new rates, which is the
   intended tripwire.
3. Bare-suffix model ids (e.g. `haiku-4-5` vs `claude-haiku-4-5`) resolve
   through `PipelineStepConfigResolver`'s tolerant lookup before reaching
   the table, so you do not need a row per alias.
