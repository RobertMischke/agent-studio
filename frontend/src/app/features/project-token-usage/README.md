# project-token-usage

Slice 8 of the quality-system mockup: per-project token spend with category split (`job` / `supporting` / `orchestrator`), heatmap, and expensive-job drill-down.

## Public API

Imports via `from './features/project-token-usage'`. See [`index.ts`](./index.ts).

**Component**: `ProjectTokenUsagePanelComponent` — the rail panel itself; rendered inside the project shell when the user picks the "token-usage" rail.

**Types**:

- `ProjectTokenCategory` — `'job' | 'supporting' | 'orchestrator'`.
- `ProjectTokenUsageSummary` — lifetime + last-24h totals split by category.
- `ProjectTokenHeatmap`, `ProjectTokenHeatmapJob`, `ProjectTokenHeatmapCell` — daily heatmap.
- `ProjectExpensiveJob`, `ProjectExpensiveJobsResponse` — top-N expensive jobs.
- `ProjectJobTokenDetail`, `ProjectJobTokenRun` — per-job drill-down with run-by-run breakdown.

## Notable

- The category split (`job` / `supporting` / `orchestrator`) follows `taxonomy.md` — supporting calls are summary generation, orchestrator calls are manager-style chats.
- E2E perf budget: mounting + heatmap interactions stay under 200 ms cumulative (`e2e/project-token-usage-panel.spec.ts`).
