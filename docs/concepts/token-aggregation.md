---
id: token-aggregation-concept
title: "Token aggregators became bus-backed shims (concept + living knowledge)"
status: active
category: concept
updatedAt: 2026-06-09
last-updated: 2026-06-09
reason: "Concept + knowledge-collection page for the token-aggregation domain (ASS-881)"
taskKey: ASS-1717
tags: [token-aggregation, bus-backed-shim, drift-rule, observability, cost]
related-tasks: [ASS-881, ASS-1717]
related-adrs: []
related-docs:
  - "docs/domains/tokens.md"
  - "docs/domains/token-pricing.md"
  - "docs/contracts/code-patterns.md"
  - "docs/architecture/bus/agent-message-bus.md"
---

# Token aggregators -> bus-backed shims

> **What this page is.** A living concept and knowledge-collection page for the
> token-aggregation area of the backend. It explains *why* every token roll-up
> now reads from one source (the Agent Message Bus), *what* the four legacy
> aggregators were and why they became thin shims, and *how* the architecture
> guard test stops the old pattern from creeping back. New findings about this
> area belong in the [Living knowledge log](#living-knowledge-log) at the
> bottom. The engineering plan and phase-by-phase migration record live in the
> token domain doc: [`docs/domains/tokens.md`](../domains/tokens.md).

## TL;DR

- There used to be **five** independent token-spend aggregators. Three of them
  read the *same* file (`orchestrator.jsonl`) and each rounded, filtered, and
  categorised differently, so the "tokens today" number disagreed across the UI.
- The fix is one canonical interface, **`ITokenAggregator`**, backed by one
  source of truth, the **Agent Message Bus** (`logs/bus/*.jsonl`).
- **ASS-881 (Refactor Phase 4)** converted the four legacy aggregators into
  *bus-backed shims*: their pure math is reused, but the input now comes from
  the bus instead of each surface re-reading log files its own way.
- A drift rule (`token-aggregation-canonical`) plus an **architecture guard
  test** (`TokenAggregationCanonicalDependencyTest`) prevent any new code from
  injecting the old concrete aggregators again.

## Why bus-backed: the drift problem

Every observability surface needs a token number: the workspace timeline, the
Project-Detail Token-Usage panel, the status-bar usage modal, the kanban
card footer, the ad-hoc-usage chart. Before consolidation each surface owned its
own aggregator. Because each one rounded, filtered, and categorised in its own
way, the same underlying spend produced slightly different totals depending on
which screen you looked at. That is classic *aggregation drift*.

This is the same drift pattern the codebase already eliminated elsewhere:
one-shot CLI invocations (`ICliOneShot`), JSONL appends (`IJsonlAppender`), and
frontmatter parsing (`FrontmatterParser`). The cure is always the same shape:

1. **One canonical surface** that is the union of every consumer's needs:
   `ITokenAggregator`.
2. **One source of truth**: the Agent Message Bus. Every run, supporting call,
   and orchestrator decision already emits a `kind=token-usage` message to the
   bus, so the bus holds the complete spend picture (and it also carries the
   newer `contextWindow` / `latency` fields the old log readers never knew
   about).
3. **A drift rule** that flags any new ad-hoc roll-up so the problem cannot
   silently return.

## The canonical chain

```
consumers (endpoints, job-card footer, status bar)
        |
        v
ITokenAggregator                         <- the only thing new code depends on
        |  (interface: backend/Services/Tokens/ITokenAggregator.cs)
        v
TokenAggregationService                  <- the single implementation
        |  (backend/Services/Tokens/TokenAggregationService.cs)
        |
        +--> BusAggregationCache          (bus-native rollup: byModel/byParticipant/byDay)
        +--> BusBackedTokenSummaryReader      (lifetime + per-model + USD, per-job footer)
        +--> BusBackedWorkspaceTimelineReader (project x time-bucket cells)
        +--> BusBackedProjectTokenUsageReader (summary, heatmap, expensive-jobs, drill-down)
        +--> BusBackedAdHocUsageReader        (TitleGen / SummaryGen / ... one-shot rollup)
                        |
                        v
                AgentMessageBusStore  ->  logs/bus/*.jsonl   (source of truth)
```

`TokenAggregationService` does almost no math itself. Each method delegates to
the matching bus-backed reader. The readers, in turn, do not re-derive the math
either: they convert bus messages into transient log entries via
`BusTokenEntryConverter` and then call the **same pure-function folds** that the
legacy services always used. That is the trick that makes parity exact by
construction: model-key normalisation, USD estimation through `TokenPricing`,
day-bucket formatting, and the Job/Supporting/Orchestrator split all run through
one code path. The bus reader only swaps the *input source*.

## What the legacy aggregators were, and why they became shims

Four services predated `ITokenAggregator`. Each one read log files directly and
produced its own shape for one surface:

| Legacy service | Surface it fed | Read from | What it computed |
|---|---|---|---|
| `AdHocUsageService` | ad-hoc usage chart (`GET /api/adhoc/usage`) | `adhoc-usage.jsonl` | per-source / per-day / per-model rollup of one-shot Haiku calls (TitleGen, SummaryGen, PromptEnhance, CommitMessage, ...) |
| `WorkspaceTokensTimelineService` | workspace timeline (`GET /api/workspace/tokens`, `#/workspace/tokens`) | `orchestrator.jsonl` for every watched project | `(project, time-bucket)` cells with priced dollars |
| `ProjectTokenUsageService` | Project-Detail Token-Usage panel (`GET /api/projects/{project}/token-usage/*`) | `orchestrator.jsonl` + job-folder scan | lifetime/24h summary with Job/Supporting/Orchestrator split, per-day x per-job heatmap, expensive-jobs top-N, per-job drill-down with deltas |
| `TokenSummaryService` (+ `TokenSummary`) | project-card last-usage, status-bar usage modal, per-job card footer | `orchestrator.jsonl` | per-project lifetime totals + per-model split + estimated USD; workspace aggregate |

Three of these (`WorkspaceTokensTimelineService`, `ProjectTokenUsageService`,
`TokenSummaryService`) read the **same** `orchestrator.jsonl` and produced three
different shapes. That overlap was the drift source.

**What "became a shim" means here.** The service files were *not* deleted. ASS-881
kept their pure static fold functions (the actual math) and parity fixtures, but
moved the runtime read path to a bus-backed reader that reuses those folds:

- `TokenSummaryService.Summarize(...)` / `AggregateSummaries(...)` are reused by
  `BusBackedTokenSummaryReader`.
- `WorkspaceTokensTimelineService.BuildFromEntries(...)` is reused by
  `BusBackedWorkspaceTimelineReader`.
- `ProjectTokenUsageService.Build*` folds are reused by
  `BusBackedProjectTokenUsageReader`.
- `AdHocUsageService.Aggregate(...)` is reused by `BusBackedAdHocUsageReader`.

So the legacy classes survive as **pure-function libraries plus parity
fixtures**, not as injectable runtime aggregators. The bus reader is the shim
that adapts the new input source onto the old, trusted math.

### Why keep them at all instead of deleting?

1. **Parity tests.** Each surface ships a Phase-5 parity test
   (`TokenSummaryBusParityTests`, `WorkspaceTokensTimelineBusParityTests`,
   `ProjectTokenUsageBusParityTests`, `AdHocUsageBusParityTests`) that drives
   both the legacy reader and the bus reader over one fixed data set and asserts
   byte-identical numeric output. Those tests stay in the repo as regression
   guards, and they need the legacy fold to compare against.
2. **Historical-data fallback.** Very old records that predate the Phase-2 bus
   emit, and legacy `orchestrator.jsonl` entries with no `participantId`, still
   read correctly through the legacy path (which falls back to
   `SupportingJobTitlePrefixes` matching when no bus participant id is present).

One documented exception to byte-exact parity: the per-call drill-down's
user-facing `Summary` string differs (the bus mints its own
`tokens: in=... out=...` headline at emit time, while `orchestrator.jsonl`
carries the runner's own headline). All numeric fields and `Topic` are still
asserted verbatim; only that one presentation-only string is excluded.

## The canonical-dependency rule and its guard test

The rule, in one sentence: **runtime code depends on `ITokenAggregator`, never
on a legacy concrete aggregator.**

This is enforced two ways.

### 1. Architecture guard test (the hard gate)

`backend.Tests/Architecture/TokenAggregationCanonicalDependencyTest.cs` scans
every `*.cs` file under `backend/` and fails the build if it finds a line that
*injects* one of the legacy concrete types
(`TokenSummaryService`, `WorkspaceTokensTimelineService`,
`ProjectTokenUsageService`, `AdHocUsageService`) as a field or constructor
parameter.

Two things make the test precise rather than blunt:

- It matches an **injected concrete type** (e.g. `private readonly
  TokenSummaryService _tokens;` or `AdHocUsageService usage,`) but deliberately
  ignores **static calls** (`TokenSummaryService.Summarize(...)`) and DI
  registration (`builder.Services.AddSingleton<TokenSummaryService>();`). That is
  why reusing the pure folds is allowed while re-injecting the aggregator is not.
- It allow-lists the legacy files themselves plus everything under
  `backend/Services/Tokens/`, so the bus-backed readers can keep calling the
  legacy folds without tripping the guard.

If a future change injects a legacy aggregator into a runtime service, this test
goes red with a remediation message: *inject `ITokenAggregator` and use its
bus-backed methods.*

### 2. Drift rule (the soft, repo-wide signal)

`token-aggregation-canonical` in [`docs/contracts/code-patterns.md`](../contracts/code-patterns.md)
is a drift-analysis rule. It scans for the telltale markers of a hand-rolled
roll-up (`entry.TokenUsage` access, `AgentMessageTokens`, a string-keyed token
total dictionary) outside the `Tokens/` and `Bus/` namespaces, and the "good
variant" is membership in those namespaces or use of `ITokenAggregator`. Now
that Phase 4 is complete, its severity is **Warn**: any new aggregator outside
`backend/Services/Tokens/` or `backend/Services/Bus/` gets flagged.

The guard test is the build-breaking gate; the drift rule is the broader,
advisory radar that catches patterns the narrow injection regex would miss.

## How this connects to the token domain and the UI

- **Token domain doc (system of record):**
  [`docs/domains/tokens.md`](../domains/tokens.md) holds the full audit,
  the five-aggregator table, and the phase-by-phase migration record. Read it
  when you need the *plan*; read this page when you need the *concept*.
- **Pricing:** [`docs/domains/token-pricing.md`](../domains/token-pricing.md) owns the
  per-model price table. Pricing is deliberately *separate* from aggregation;
  the aggregator delegates to `TokenPricing` only for the `Dollars` field.
- **Bus:** [`docs/architecture/bus/agent-message-bus.md`](../architecture/bus/agent-message-bus.md) describes the
  channel that is now the single source of truth.
- **Schemas:** `docs/schemas/token-aggregate.schema.json`,
  `token-aggregate-by-client.schema.json`, `token-timeline-bucket.schema.json`
  pin the wire shapes.
- **Frontend surfaces that consume the endpoints:**
  - `frontend/src/app/features/tokens/` (workspace token timeline + status-bar
    summary block)
  - `frontend/src/app/features/project-token-usage/` (Project-Detail Token-Usage
    panel)
  - `frontend/src/app/features/board/components/task-card/token-popover.directive.ts`
    (kanban card footer)
  - `frontend/src/app/features/task-detail/components/prompt-pane/pipeline-token-usage/`
    (per-task pipeline cost)

## Practical guide: adding or changing a token surface

For an operator or an LLM instance working in this area, the rules of the road:

1. **Need a token number somewhere new?** Inject `ITokenAggregator` and call the
   method that matches your shape. Do not read `orchestrator.jsonl`,
   `adhoc-usage.jsonl`, or `logs/bus/*.jsonl` yourself, and do not build a
   string-keyed token dictionary. The guard test will reject an injected legacy
   aggregator, and the drift rule will warn on a hand-rolled roll-up.
2. **The shape you need does not exist on `ITokenAggregator`?** Add a method to
   the interface and implement it in `TokenAggregationService`, backed by a
   bus-backed reader. Reuse an existing pure fold if one fits.
3. **Changing the math?** Change the pure fold on the legacy service (that is
   what it is for now) so both the bus reader and the parity fixture move
   together, then update the parity test fixture under
   `backend.Tests/Fixtures/TokenAggregationParity/`.
4. **Emitting new spend?** Emit a `kind=token-usage` bus message
   (`AgentMessageBusBridge.EmitTokenUsageAsync` / `EmitTokenUsageRichAsync`) so
   the canonical aggregator can see it. Keep the existing emit instrumentation;
   do not silently drop it while editing nearby code.

## Living knowledge log

Append new findings about the token-aggregation area here, newest on top. Keep
each entry short: date, what was learned, and a pointer to the code/commit/task.

- **2026-06-09 (ASS-1717).** Page created. State of the world at creation:
  ASS-881 (Phase 4) has landed (commits `aba24661`, `d5597897`, `9bf95984`);
  all four bus-backed shims are live, the four parity tests are green, the
  architecture guard test is in place, and the drift rule severity is `Warn`.
  Open follow-ups noted in the domain doc: optional one-shot backfill of
  historical `orchestrator.jsonl` into the bus (only the lifetime-totals surface
  reads older history, and the shim can read it straight from the log until
  backfill lands).
