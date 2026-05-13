# Token Aggregation — Audit and Consolidation Plan

> **Status (2026-05-11):** Phase 4+5 complete. Every surface that
> `ITokenAggregator` exposes now reads through a bus-backed reader; the
> legacy services (`TokenSummaryService`, `WorkspaceTokensTimelineService`,
> `ProjectTokenUsageService`) stay registered for the parity-test fixture
> and for older direct callers that still go through their concrete types, but
> `TokenAggregationService` never reaches into them. Each surface ships
> with a Phase-5 parity test
> (`TokenSummaryBusParityTests`, `WorkspaceTokensTimelineBusParityTests`,
> `ProjectTokenUsageBusParityTests`, alongside the earlier
> `AdHocUsageBusParityTests`) that drives both readers over the same data
> set and asserts byte-identical numeric output. The drift rule
> `token-aggregation-canonical` is ready to graduate from `Info` to `Warn`.

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

All four shims have landed in this order:

1. **Landed.** `AdHocUsageService` read path. `BusBackedAdHocUsageReader`
   queries `support:adhoc` / `kind=token-usage` messages from the
   `_workspace` bus projection and folds them through the same pure
   aggregator function as the JSONL reader. The `AdHocUsageRecorder`
   emits to the workspace scope (`project=null`) and stamps the bus
   message's `CreatedAt` with the record's `Ts` so multi-day rollups
   match. The parity test
   [`AdHocUsageBusParityTests`](../backend.Tests/AdHocUsageBusParityTests.cs)
   drives nine realistic records (every named source, mixed days, mixed
   models including one unpriced) and asserts byte-identical output.
   The `AdHocUsageService.Aggregate` source/model ordering picked up
   stable tie-breakers in the same change so insertion-order differences
   between JSONL and bus paths can no longer leak into the output.
2. **Landed.** `TokenSummaryService.Summarize` read path.
   `BusBackedTokenSummaryReader` queries `kind=token-usage` messages
   attributed to the project's orchestrator participant
   (`orchestrator:<project>`), converts each into a transient
   `OrchestratorLogEntry`, and folds through the same pure
   `TokenSummaryService.Summarize` overload. `TokenSummaryService.Aggregate`
   was refactored so the workspace fold (`AggregateSummaries`) is a
   static helper both readers reuse - the bus path and the legacy path
   cannot disagree on cross-project rollup math. Parity test:
   [`TokenSummaryBusParityTests`](../backend.Tests/TokenSummaryBusParityTests.cs).
3. **Landed.** `WorkspaceTokensTimelineService.Build` read path.
   `BusBackedWorkspaceTimelineReader` walks every supplied project, pulls
   the orchestrator-attributed token-usage messages, and feeds them
   through `WorkspaceTokensTimelineService.BuildFromEntries`. Window
   snapping, bucket span, dollar accounting, and the per-project peak /
   last-activity trackers are unchanged because the bucketer is unchanged.
   Parity test:
   [`WorkspaceTokensTimelineBusParityTests`](../backend.Tests/WorkspaceTokensTimelineBusParityTests.cs).
4. **Landed.** `ProjectTokenUsageService.BuildSummary` /
   `BuildHeatmap` / `BuildExpensiveJobs` / `BuildJobDetail` read paths.
   `BusBackedProjectTokenUsageReader` reuses every static `*FromEntries`
   overload on the legacy service so the Job / Supporting / Orchestrator
   category split (`SupportingJobTitlePrefixes` lookup against
   `JobScannerService.ScanAllJobs`) is byte-identical across the source
   change. The canonical bus-native split (participantId `agent:*` vs
   `support:*` vs `orchestrator:*`) is a follow-up once the legacy
   surface retires - parity needed byte-exact equality first. Parity
   test:
   [`ProjectTokenUsageBusParityTests`](../backend.Tests/ProjectTokenUsageBusParityTests.cs).

Each parity test compares every numeric field plus the ordered breakdown
lists across the two readers driven against the same data set. The
per-call drill-down's user-facing `Summary` is the one documented
exception: the bus mints its own `tokens: in=... out=...` headline at
emit time while `orchestrator.jsonl` carries the runner's own headline,
so the parity assertion excludes that one presentation-only field
(numeric fields and `Topic` are still checked verbatim).

### Phase 5 — Parity tests

For each surface listed above, a fixture file fed through both the legacy
service and the new aggregator must produce byte-identical output. The
fixtures live under `backend.Tests/Fixtures/TokenAggregationParity/` and are
checked in. The tests stay in the repository after migration as regression
guards.

### Phase 6 — Drift rule

The drift rule `token-aggregation-canonical` is in
[`docs/code-patterns.md`](code-patterns.md). Phase 4 is now complete, so
the severity is ready to move from `Info` to `Warn`; the rule will then
flag any new aggregator outside `backend/Services/Tokens/` or
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
| `backend/Services/AdHoc/AdHocUsageService.cs` | Legacy aggregator; only the parity fixture still calls it directly |
| `backend/Services/Runner/ProjectTokenUsageService.cs` | Pure-function fold reused by `BusBackedProjectTokenUsageReader` |
| `backend/Services/Runner/WorkspaceTokensTimelineService.cs` | Pure-function bucketer reused by `BusBackedWorkspaceTimelineReader` |
| `backend/Services/Runner/TokenSummary.cs` | Pure-function summarizer reused by `BusBackedTokenSummaryReader`; workspace fold extracted to `AggregateSummaries` |
| `backend/Services/Tokens/BusTokenEntryConverter.cs` | Shared adapter that turns bus `kind=token-usage` messages into transient `OrchestratorLogEntry` records so the bus-backed readers reuse the legacy folds |
| `backend/Services/Tokens/BusBackedTokenSummaryReader.cs` | Phase-4 read path for the lifetime + per-model summary |
| `backend/Services/Tokens/BusBackedWorkspaceTimelineReader.cs` | Phase-4 read path for the workspace timeline |
| `backend/Services/Tokens/BusBackedProjectTokenUsageReader.cs` | Phase-4 read path for the four project-detail surfaces |
| `backend/Endpoints/Jobs/JobEndpointHelpers.cs` | Job-card token footer lookup through `ITokenAggregator.WorkspacePerJob` |
