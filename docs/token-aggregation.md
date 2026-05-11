# Token Aggregation — Audit and Consolidation Plan

> **Status (2026-05-11):** Audit + Phase 2 (bus emission for ad-hoc calls) +
> Phase 3 (interface skeleton) + Phase 6 (drift rule) landed in this commit.
> Phases 4 (legacy-service migration to shims) and 5 (parity tests) are queued
> as follow-up tasks. See "Migration order" below.

## Why this document exists

Five backend services compute token-spend aggregations independently, each
with its own source file, output shape, and consumer. On every observability
surface (workspace timeline, project-detail token-usage panel, job-card
footer, ad-hoc-usage chart) the "tokens today" number is *slightly* different
because each aggregator rounds, filters, and categorises in its own way.

This is the same drift pattern the codebase already eliminated for CLI
invocations (`ICliOneShot`), JSONL appends (`IJsonlAppender`), and frontmatter
parsing (`FrontmatterParser`). The fix is the same shape: one canonical
aggregator (`ITokenAggregator`), one source of truth (the Agent Message Bus),
and a drift rule that flags new ad-hoc roll-ups.

## The five duplicated aggregators

| # | Service | Source file | Reads | Produces | Consumed by |
|---|---------|-------------|-------|----------|-------------|
| 1 | `AdHocUsageService` (read path) over `AdHocUsageRecorder` | `backend/Services/AdHoc/AdHocUsageService.cs`, `AdHocUsageRecorder.cs` | `adhoc-usage.jsonl` (workspace-wide) | Per-source / per-day / per-model rollup of one-shot Haiku calls | `GET /api/adhoc/usage` — ad-hoc usage chart in the status-bar modal |
| 2 | `ProjectTokenUsageService` | `backend/Services/Runner/ProjectTokenUsageService.cs` | `orchestrator.jsonl` (per project) + job-folder scan | Lifetime/24h summary with Job/Supporting/Orchestrator split; per-day × per-job heatmap; expensive-jobs top-N; per-job drill-down with deltas | `GET /api/projects/{project}/token-usage/*` — Project-Detail Token-Usage panel |
| 3 | `WorkspaceTokensTimelineService` | `backend/Services/Runner/WorkspaceTokensTimelineService.cs` | `orchestrator.jsonl` for *every* watched project | (project × time-bucket) cells with priced dollars | `GET /api/workspace/tokens` — `#/workspace/tokens` stacked timeline |
| 4 | `TokenSummaryService` + `TokenSummary` | `backend/Services/Runner/TokenSummary.cs` | `orchestrator.jsonl` (per project) | Per-project lifetime totals + per-model split + estimated dollars; aggregate across all projects | Project-card last-usage, status-bar usage modal, `JobEndpointHelpers.WithRuntime` per-job rollups |
| 5 | `BusAggregationCache` (the canonical one) | `backend/Services/Bus/BusAggregationCache.cs` | `logs/bus/*.jsonl` via `AgentMessageBusStore` | `byModel` / `byParticipant` / `byDay` totals plus context-window and latency awareness | `GET /api/bus/{project}/token-aggregate` |

Three of these (#2, #3, #4) read the *same* file (`orchestrator.jsonl`) and
each produce a different shape. Two (#1, #5) read different files. None of
the four legacy aggregators are aware of the `contextWindow` / `latency`
fields that the bus messages now carry.

### What each service categorises differently

- **`ProjectTokenUsageService`** splits spend into `Job` / `Supporting` /
  `Orchestrator` by matching the job's title prefix against
  `SupportingJobTitlePrefixes`. The bus uses `participantId` (`agent:*` vs
  `support:*` vs `orchestrator:*`) — semantically equivalent but driven by a
  different field.
- **`AdHocUsageService`** splits by `Source` (TitleGen, SummaryGen,
  PromptEnhance, RoadmapIntake, ...) — these never reach the bus today.
- **`WorkspaceTokensTimelineService`** buckets by `(project, time-bucket)`
  with a configurable window/bucket size (1h/6h/24h/168h × 5/15/60min).
- **`TokenSummaryService`** estimates dollars through `TokenPricing` for
  every reader.
- **`BusAggregationCache`** rolls `byParticipant` and `byDay` (UTC) but does
  **not** yet expose per-job rows, expensive-jobs top-N, or the
  Job/Supporting/Orchestrator category split that
  `ProjectTokenUsageService` needs.

## Target state — `ITokenAggregator`

The canonical aggregator is the union of all five consumer needs. The
interface is defined in
[`backend/Services/Tokens/ITokenAggregator.cs`](../backend/Services/Tokens/ITokenAggregator.cs).
The shape:

```csharp
public interface ITokenAggregator
{
    // Workspace-wide rollups
    TokenAggregateResponse ForProject(string project, DateTime? since = null, DateTime? until = null);
    IReadOnlyList<TokenAggregateBucket> ForWorkspaceTimeline(int windowHours, int bucketMinutes, DateTime? nowUtc = null);

    // Per-project breakdowns the Project-Detail panel needs
    ProjectTokenUsageSummary ProjectSummary(string project, string watchPath, DateTime? nowUtc = null);
    ProjectTokenHeatmap ProjectHeatmap(string project, string watchPath, int days, DateTime? nowUtc = null);
    IReadOnlyList<ProjectExpensiveJob> ProjectExpensiveJobs(string project, string watchPath, int limit);
    ProjectJobTokenDetail? ProjectJobDetail(string project, string watchPath, string jobId);

    // Lifetime / dollars surface (status-bar usage modal)
    TokenSummary LifetimeSummary(string project, string watchPath);
    TokenSummaryAggregate WorkspaceAggregate(IEnumerable<(string Name, string WatchPath)> projects);

    // Ad-hoc-call rollup (TitleGen, SummaryGen, ...)
    AdHocUsageAggregate AdHocAggregate(DateTime? since = null);
}
```

The first implementation, `TokenAggregationService`, lives next to the
interface and currently delegates to `BusAggregationCache` plus the legacy
aggregators while Phase 4 migrates each consumer over one at a time. New code
must depend on `ITokenAggregator` rather than the legacy services directly.

## Migration order

Phases 1 (this audit), 2 (bus emission for ad-hoc calls), 3 (interface +
delegating service), and 6 (drift rule) ship in the same commit that produced
this document. The remaining phases are:

### Phase 4 — Convert legacy services to bus-backed shims

Land one shim at a time, in this order, so each migration is independently
verifiable:

1. `AdHocUsageService` (read path). Today its records are workspace-wide and
   have no project scope, so the bus path requires a `project: "(workspace)"`
   convention or a workspace-scoped bus folder. The write path
   (`AdHocUsageRecorder`) stays because it is the legacy disk format readers
   of `adhoc-usage.jsonl` expect, but Phase 2 already mirrors every record
   onto the bus, so the read path can switch.
2. `TokenSummaryService.Summarize` (lifetime per-project totals + per-model
   split). Simplest of the orchestrator-log readers; pure-function fold.
3. `WorkspaceTokensTimelineService.Build`. Bucketing logic is straightforward
   once the bus carries every orchestrator turn.
4. `ProjectTokenUsageService.BuildSummary` / `BuildHeatmap` /
   `BuildExpensiveJobs` / `BuildJobDetail`. Most surface area; requires the
   bus to carry the Job/Supporting/Orchestrator category (today derivable
   from `participantId`).

Each shim ships with a parity test (Phase 5) that compares the legacy output
against the bus-backed output over a fixed JSONL fixture; the shim only
lands when parity is byte-exact.

### Phase 5 — Parity tests

For each surface listed above, a fixture file fed through both the legacy
service and the new aggregator must produce byte-identical output. The
fixtures live under `backend.Tests/Fixtures/TokenAggregationParity/` and are
checked in. The tests stay in the repository after migration as regression
guards.

### Phase 6 — Drift rule

The drift rule `token-aggregation-canonical` is in
[`docs/code-patterns.md`](code-patterns.md) but **starts at `Info` severity**
until Phase 4 completes. Once the four legacy services have been converted
to shims (or removed), the severity moves to `Warn` and the rule starts
flagging any new aggregator outside `backend/Services/Tokens/` or
`backend/Services/Bus/`.

The candidate marker scans for the two telltale patterns:

- `entry.TokenUsage` access outside `Services/Tokens/` / `Services/Bus/`
- A `Dictionary` of token totals keyed by string that doesn't go through
  `ITokenAggregator`

The good variant is membership in the `Tokens` or `Bus` namespace.

## What's deliberately out of scope

- **Token pricing tables.** `TokenPricing` stays where it is; cost lookup is
  separate from aggregation. The aggregator delegates to it for the
  `Dollars` field on the bus response.
- **CLI quota** (`/api/cli/quota`). Different source (subscription window),
  different cadence, different consumer.
- **Backfill of historical orchestrator.jsonl into the bus.** Optional
  one-shot — the bus has been live long enough that current spend is on it;
  the only consumer of older history is the lifetime totals surface, which
  the shim can read straight from `orchestrator.jsonl` until backfill lands.

## Reference — file paths

| File | Role after consolidation |
|------|--------------------------|
| `backend/Services/Tokens/ITokenAggregator.cs` | Canonical interface |
| `backend/Services/Tokens/TokenAggregationService.cs` | Implementation, delegates to the bus and (during migration) legacy services |
| `backend/Services/Bus/BusAggregationCache.cs` | In-memory rollup over `logs/bus/*.jsonl` — source of truth |
| `backend/Services/Bus/AgentMessageBusBridge.cs` | Producer side: `EmitTokenUsageAsync` / `EmitTokenUsageRichAsync` |
| `backend/Services/AdHoc/AdHocClaudeInvoker.cs` | Ad-hoc-call recorder; **also fires `EmitTokenUsageAsync` after Phase 2** |
| `backend/Services/AdHoc/AdHocUsageRecorder.cs` | Legacy write path (kept for disk-format readers) |
| `backend/Services/AdHoc/AdHocUsageService.cs` | Will become a shim in Phase 4 |
| `backend/Services/Runner/ProjectTokenUsageService.cs` | Will become a shim in Phase 4 |
| `backend/Services/Runner/WorkspaceTokensTimelineService.cs` | Will become a shim in Phase 4 |
| `backend/Services/Runner/TokenSummary.cs` | Will become a shim in Phase 4 |
