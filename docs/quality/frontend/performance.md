# Frontend performance playbook

This is the working playbook for frontend perf in Agent Software Studio. It
captures decisions taken across Cycles 3-5 of the performance overhaul plus
the patterns we want every new component to follow without us having to ask
in review.

> Measurement first. Numbers in commit messages, not adjectives. The
> reproducibility recipe lives at the bottom of this doc.

## Hard rules

These are non-negotiable. If a PR violates one, push back without ceremony.

1. **Every recurring timer pauses when `document.hidden`.** Use
   `setVisibleInterval` from
   [src/app/utils/visible-interval.ts](../../../frontend/src/app/utils/visible-interval.ts).
   Bare `setInterval` for HTTP polling is a perf bug; the only legitimate
   uses are local clock ticks where staleness on tab return would surprise
   the user (`now-tick.service.ts` is the documented exception).
2. **Polled endpoints must serve from the backend snapshot cache, not from
   `JobScannerService.ScanAllJobsRaw`.** The cache is fronted by
   `JobIndexCache` (Cycle 1) and invalidated by `JobWatcherService` events
   plus explicit calls from mutation services (Cycle 2). New endpoints
   that poll-call into the scanner go through the cache by default.
3. **The board polls 2 s. Detail panes 5-10 s. Singletons 15-30 s.** If a
   new poller wants a sub-2 s cadence, it needs an architectural
   justification in the PR description (e.g. "user-typed input completion
   feedback") and a fallback to push if/when the SignalR client lands
   (Cycle 5).
4. **Bounded buffers in the browser.** Live streams shown in the UI
   (CLI output, bus feed, tool-calls, observations) keep last N rendered
   lines (default N = 1000). Full logs stay on disk; the frontend never
   becomes the durable store.
5. **No `await page.waitForLoadState('networkidle')` in perf specs.** If
   the regression we are testing for is "the network never goes idle",
   `networkidle` makes the test fail with a 15 s infrastructure timeout
   and hides the actual latency reading. Use `domcontentloaded` plus a
   short `waitForTimeout` to let the first poll fire, then assert the
   number directly.

## Patterns to follow

### Orchestrator API load shedding

- The board refreshes only `/api/tasks/grouped`. `TaskService` derives the flat
  task list from that response, so one heartbeat never requests the same task
  snapshot twice.
- Task-index watcher events invalidate the snapshot only for semantic
  `task.json` changes and task-folder moves or deletes. Heartbeats such as
  `lastProgressAt`, logs, results, and generated sidecars must not trigger a
  full workspace scan. Bursts are coalesced per task.
- Snapshot refreshes are single-flight. External readers may use the last
  published snapshot while a refresh is running; mutation paths wait for the
  required generation so read-after-write remains consistent.
- Board merge and publish projections are keyed by lightweight Git ref
  fingerprints. An unchanged board heartbeat starts no Git process. Ref
  movement invalidates immediately, while a 10-minute TTL remains as a safety
  refresh for unusual repository layouts. Cold projections use bounded Git
  concurrency and combine related reachability checks.
- A task detail view fetches an inactive task once. Recurring task polling is
  reserved for preparation, active progress, auto-review, or a running
  execution, and requests for the same task must never overlap.

### Polling

- Use `setVisibleInterval(fn, ms)` instead of `setInterval` for any
  timer that fetches over HTTP or invalidates a signal that templates
  read.
- Use `clearVisibleInterval(handle)` in `ngOnDestroy`. The handle type
  is `VisibleIntervalHandle`; assignable to and from `ReturnType<typeof
  setInterval>` so the wrapper is a drop-in.
- Reference-counted polling (e.g. `git-summary.service.ts`,
  `git-hygiene.service.ts`, `auto-review-status.store.ts`) is the right
  shape when multiple components need the same data: first subscriber
  arms the timer, last unsubscriber tears it down.
- Per-job pollers (`cli-output-poll`, `run-timeline-poll`,
  `session-events-poll`, `screenshots-poll`, `claude-session-poll`,
  `git-pane`) live as `@Injectable()` (no `providedIn`) services so each
  detail-pane instance owns its own timer; the pattern keeps cross-tab
  state from leaking and the `ngOnDestroy` cleanup is automatic.
- Chained `setTimeout` poll loops (the only legitimate one today is
  `cli-output-poll.service.ts`'s `startPolling`) handle visibility
  inline with an early-return that re-arms the timer:

  ```ts
  if (typeof document !== 'undefined' && document.hidden) {
    this.pollTimeout = setTimeout(poll, 2000);
    return;
  }
  ```

### Lifecycle of detail panes

- Per-component pollers must respond to `syncTo(info)` with: stop the
  current timer, refresh once, re-arm. Pre-Cycle-3 code that just calls
  `start()` on every input change will run two timers in parallel; the
  service implementations in `frontend/src/app/components/job-detail/`
  are the reference shape.
- When a pane is collapsed (not visible to the user) inside an open
  parent, the pane should release its pollers. Cycle 4 will introduce
  helpers for this; until then, the parent is responsible for calling
  `stop()` on the child's poll service when it hides the pane.

### State and rendering

- Prefer signals over BehaviorSubject. `inject(JobService).grouped()`
  is cheap and keeps change-detection deterministic.
- `OnPush` is the default for new components. If a component reads
  shared mutable state that changes outside the input flow, the read
  must go through a signal so the change-detector picks it up; raw
  `Date.now()` reads in templates produce NG0100 in dev mode.
- Track lists by stable id (`trackBy: 'id'` for `*ngFor`). The kanban
  card list is the load-bearing example: a missing `trackBy` re-renders
  every card on every poll.
- Heavy computeds (counts, filters, sorts) memoize naturally as long
  as their reads are signals. If a computed reads `Date.now()` it will
  invalidate every change-detection cycle; route it through the
  `NowTickService.now()` signal instead.

### Task timeline budget addendum

The Task Timeline has a dedicated 1,000-event browser budget. The performance
fixture scales the unchanged event and payload distributions from real tasks
AGT-2577 and QS-72, then measures each scenario independently:

| Metric at 1,000 ledger events | Budget |
|---|---:|
| Timeline response to painted rows | <= 1,000 ms |
| Host-normalised animated scroll over the mounted window | >= 55 FPS |
| Mounted timeline rows on first render | <= 50 |
| Timeline DOM nodes on first render | <= 3,000 |
| Collapsed payload values in the DOM | 0 |

Run the budget probe against the current frontend with:

```sh
RUN_TIMELINE_PERF=1 \
TIMELINE_PERF_EXPECT_WINDOWED=1 \
TIMELINE_PERF_PHASE=after \
npx playwright test e2e/perf/task-timeline-performance.spec.ts --project=chromium --workers=1
```

Timeline rows have variable height because payloads, findings, and markdown can
expand. Use the progressive timeline window as the virtualisation strategy:
mount the newest 50 rows, prepend older rows in pages of 50 on request, and
track every row by the stable ledger identity. Closed payload disclosures must
mount only their summary control. Their values, source lists, prompts, and
context enter the DOM when opened and leave it again when closed.

Live ledger updates use `timelineEventAppended` to append one sanitised row.
The slower HTTP snapshot remains the convergence path and must reconcile an
unchanged prefix without replacing its object identities. This limits an event
to the affected window edge instead of rebuilding the full stream.

AGT-2631 owns the Timeline information redesign. Its categories, popovers, and
newest-first presentation must consume this windowing, disclosure, and append
infrastructure as their technical base. It must not add a second virtualiser or
restore eager payload DOM.

The browser probe records raw scroll FPS and the idle animation-frame rate in
alternating adjacent frames over the same four-second sample. The enforced
scroll value is normalised to a 60 Hz frame source. This paired sampling keeps
shared-runner CPU contention visible in the raw evidence while making the
budget express the Timeline's incremental scroll cost rather than a load change
between two separate samples.

### Network discipline

- Polled endpoints return JSON shaped for the consumer. The board
  receives a thin "card" shape; the detail pane fetches the heavy
  shape on open. Don't grow the polled payload to "save a request"
  later - the second request for detail-only data is cheaper than
  every poll paying for it.
- Cursor/delta APIs land in Cycle 5 for output / bus / tool-calls /
  runtime streams. Until then, `cli-output-poll` returns the full
  rolling buffer; the cap on the frontend side is the
  `polledOutput.set(...)` call inside the component.
- Optimistic UI is fine when the user clicks something (drag-and-drop,
  CLI-type select, model select). The `JobService` already maintains a
  300-1500 ms suppression window for the next poll so the optimistic
  state is not yanked back to a stale snapshot. New write paths that
  need the same protection should reuse `pendingPersistCount` /
  `pendingGroupedSuppressUntil`.

## Anti-patterns

- `setInterval(() => fetch(...), 2000)` without a visibility guard - a
  user with the tab open in the background pays your polls forever.
- `await page.waitForLoadState('networkidle')` in any spec - see the
  hard rule.
- Unbounded `polledOutput` arrays in signals - the buffer must be
  capped (default 1000 lines), the rest stays on disk.
- Per-card git status calls on the board. Project-level git status
  sampled at controlled cadence is fine; one git call per kanban card
  on every poll is the regression class
  `JobsEndpointPerfTests.WithRuntime_Over200Jobs_FinishesWellUnderOneSecond`
  was written to catch.
- `ScanAllJobs` direct from a polled endpoint - go through
  `JobIndexCache` (Cycle 1). The future `ITaskAccess` (ADR-0024) is
  where the longer-term home of this contract lives, but until that
  ships, the cache is the gate.

## Measurement

Three layers, all reproducible:

| Layer | Tool | Command |
|---|---|---|
| Backend in-process | xUnit + Stopwatch | `RUN_PERF_BASELINE=1 PERF_SCENARIO=<tag> dotnet test backend.Tests --filter BackendBaselineTests` |
| Live HTTP (real backend) | Node + fetch | `node tools/perf-report/measure-live-api.mjs <tag>` |
| Browser (per-surface) | Playwright | `RUN_PERF_BASELINE=1 PERF_SCENARIO=<tag> PERF_RUN_TAG=<tag>-runN npx playwright test e2e/perf-baseline.spec.ts` |

After all three have written their JSON to `logs/perf/`, render the HTML
report:

```
node tools/perf-report/generate.mjs --scenarios baseline --scenarios after-cycle-N --out logs/perf/perf-after-cycle-N.html
```

Targets (the user-visible bar these cycles are measured against):

| Endpoint | p95 target |
|---|---|
| `/api/runner/status` | < 50 ms |
| `/api/tasks` | < 100 ms |
| `/api/tasks/grouped` | < 100 ms |
| `/api/tasks/{id}` | < 50 ms |
| `/api/tasks/{id}/output` | < 50 ms |
| `/api/tasks/{id}/runs` | < 50 ms |

`/api/cli/usage` is exempt; it walks per-CLI session histories on disk and
is intrinsically slow. Cycle 7 explores keeping CLI processes alive
(persistent worker) instead of spawning per probe; until then, the
frontend trades cadence for cost (15 s → 60 s default, see
`cli-usage-sheet.ts`).

## Pre-Cycle-3 baseline reference

For comparison when judging future regressions: the Vorher report at
`logs/perf/perf-vorher-2026-05-09.html` (also mirrored to the workspace
under `agent-taskboard-workspace/logs/analysis/_workspace/`) captured
the live HTTP cost before the cache + watcher fixes:

| Endpoint | Vorher p95 | After C1 p95 | After C2 p95 |
|---|---:|---:|---:|
| `/api/runner/status` | 205 ms | 0.5 ms | 0.4 ms |
| `/api/tasks/grouped` | 103 ms | 6.7 ms | 7.4 ms |
| `/api/tasks` | 90 ms | 5.5 ms | 6.2 ms |
| `/api/tasks/{id}/runs` | 227 ms | 0.7 ms | 0.6 ms |
| `/api/tasks/{id}/output` | 147 ms | 0.7 ms | 0.8 ms |
| `/api/tasks/{id}` | 154 ms | 2.4 ms | 1.9 ms |
| `/api/cli/usage` | 2171 ms | 2096 ms | 2084 ms |

Frontend per-surface (idle 10 s window, dev-server build):

| Surface | requests | bytes |
|---|---:|---:|
| Board | 15 | 5.6 MB |
| Project-Detail | 42 | 8.5 MB |
| Task-Detail | 19 | 5.6 MB |

Network counts haven't moved in C1/C2 (same poll cadence, just cheaper
backend). Cycle 3 (this doc) reduces them to ~zero when the tab is
hidden; Cycle 4-5 reduces them in the foreground via lazy panels and
delta/cursor APIs.
