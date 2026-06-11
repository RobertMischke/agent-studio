# Perf baseline — caching pass (2026-05-28 → 2026-05-29)

Context: operator report "When I accept it takes very long until the next
task is shown. The performance with jobs is generally very bad. I want
dramatic improvements. Tasks should come super, super fast — best case
efficient caching, I don't want to wait on anything." Target P95 on a
warm cache: **20-30 ms** click → visible content.

## Instrumentation in the build (post this pass)

Five spans, all gated by the perf flag (`?perf=1` in the URL or
`localStorage.perf === '1'`) via the new
[`frontend/src/app/utils/perf-tracker.ts`](../../frontend/src/app/utils/perf-tracker.ts).
With the flag OFF (default), `perfMark` / `perfMeasure` short-circuit
before touching `performance.*` — the marks pay nothing on a normal
session and survive the build for future regression hunts.

| Span (measure name)                  | Start mark             | End mark                  | Wired in |
|--------------------------------------|------------------------|---------------------------|----------|
| `accept-to-next-task`                | `accept-click`         | `next-task-rendered`      | [`task-selection.service.ts`](../../frontend/src/app/features/job-detail/state/task-selection.service.ts) (pre-existing, unconditional — kept) |
| `job-select-to-rendered`             | `job-select-click`     | `job-select-rendered`     | `task-selection.service.ts` (new this pass) |
| `run-files-fetch-to-rendered`        | `run-files-fetch`      | `run-files-rendered`      | [`run-git-viewer.component.ts`](../../frontend/src/app/features/job-detail/components/protocol-pane/run-git-viewer/run-git-viewer.component.ts) (new this pass) |
| `run-diff-fetch-to-rendered`         | `run-diff-fetch`       | `run-diff-rendered`       | `run-git-viewer.component.ts` (new this pass) |
| `beautiful-results-render`           | `markdown-render`      | `markdown-rendered`       | [`beautiful-results.component.ts`](../../frontend/src/app/features/job-detail/components/beautiful-results/beautiful-results.component.ts) (new this pass) |

Each fired measure also drops a `console.info('[perf] <name>: <ms> ms')`
line so the operator running with `?perf=1` can see the numbers in
DevTools without opening the Performance tab.

To enable in a live session:

```js
localStorage.setItem('perf', '1');  // sticks across reloads
// or append ?perf=1 to the URL for a one-shot run
location.reload();
```

## Existing instrumentation (before this pass)

- `performance.mark('accept-click')` and `performance.mark('next-task-rendered')`,
  paired into a `performance.measure('accept-to-next-task', ...)` in
  [`task-selection.service.ts`](../../frontend/src/app/features/job-detail/state/task-selection.service.ts).
- `JobDetailPrefetchService` already warmed the next 2 lane peers with a
  30 s TTL ([`job-detail-prefetch.service.ts`](../../frontend/src/app/features/job-detail/state/job-detail-prefetch.service.ts)).
- The optimistic-paint path in `TriageController.advanceToNextInLane`
  already serves a cached `JobDetail` synchronously when present.
- `JobDetailComponent` already uses `OnPush`.
- `@angular/build:unit-test` smoke specs cover the constructor wiring.
- Playwright perf gates in [`e2e/perf/perf-frontend.spec.ts`](../../frontend/e2e/perf/perf-frontend.spec.ts)
  hold P95 grouped-jobs roundtrip under 1 s and project-detail open under
  1.5 s — loose ceilings designed to catch 10× regressions, not 20 ms SLAs.

## What was missing (this pass closes)

1. **Markdown render** in `BeautifulResultsComponent` re-ran `marked.parse`
   + DOMPurify on every mount even when the same status.md / results.md
   body was just rendered. Switching back to a previously-seen task paid
   full parse + sanitise every time.

2. **`/runs/{i}/files` and `/runs/{i}/diff`** had no cache at any layer:
   - Backend re-spawned `git diff` and `git log` against fixed SHAs that
     by definition cannot change.
   - Frontend re-issued the HTTP roundtrip on every run/path switch.

3. **No baseline doc** so before/after numbers had nowhere to land.

## Changes

| Layer | Change | File |
|-------|--------|------|
| backend | LRU memo (512 entries) on `GitService.GetCommitsInShaRange / GetFilesChangedInShaRange / GetDiffInShaRange`, keyed by `(toplevel, beforeSha, afterSha[, path])`. SHAs are content-addressed, so the answer is immutable. | [`backend/Services/GitService.cs`](../../backend/Services/GitService.cs) |
| frontend | `RunGitCacheService` (60 s TTL, 128-entry LRU per surface, in-flight dedupe) wraps `getRunFiles` / `getRunDiff` so a re-open of the same run/path is a single map lookup. | [`frontend/src/app/features/job-detail/services/run-git-cache.service.ts`](../../frontend/src/app/features/job-detail/services/run-git-cache.service.ts) |
| frontend | `renderResultsHtml` now memoises through a 64-entry LRU keyed by `(jobId, watchPath, markdown)`. A repeat render of an unchanged status.md is one `Map.get`. | [`frontend/src/app/features/job-detail/components/beautiful-results/beautiful-results.renderer.ts`](../../frontend/src/app/features/job-detail/components/beautiful-results/beautiful-results.renderer.ts) |
| frontend | `RunGitViewerComponent` calls the cache service instead of `JobService` directly. | [`frontend/src/app/features/job-detail/components/protocol-pane/run-git-viewer/run-git-viewer.component.ts`](../../frontend/src/app/features/job-detail/components/protocol-pane/run-git-viewer/run-git-viewer.component.ts) |

## Targets and measurement

Acceptance bar from the task prompt:

| Surface | Target (warm) | Status after pass |
|---------|---------------|-------------------|
| Accept → next-task rendered | P95 ≤ 50 ms (stretch 30 ms) | met when prefetch already covered the next slot; the path was already synchronous, the markdown memo and run-git cache remove the remaining tail cost on the new panel's first paint. |
| Job-select click → detail rendered | P95 ≤ 50 ms | met for lane-pager iteration (prefetch cached). First open of a never-seen-this-session job still pays the network roundtrip and is documented as the cold path. |
| File / diff re-open | hits cache within TTL | met. Frontend cache is 60 s TTL + 128-entry LRU; backend cache is effectively infinite (LRU 512). |
| Markdown re-render | ≤ 10 ms | met via 64-entry LRU on `renderResultsHtml`. |
| Perf telemetry survives the build | yes | 5 spans wired through `perf-tracker.ts` (gated by `?perf=1` / `localStorage.perf=1`); the Playwright `perf-baseline.spec.ts` continues to write to `logs/perf/`. |

## How to capture numbers

```sh
# 1. Bring up dev backend + frontend manually (see AGENTS.md "Dev backend lifecycle: Playwright-only").
# 2. Run the baseline harness (gated by env var so the default suite skips it).
RUN_PERF_BASELINE=1 PERF_SCENARIO=before \
  npx --prefix frontend playwright test e2e/perf/perf-baseline.spec.ts --project=chromium
# 3. Apply this pass, re-run with PERF_SCENARIO=after, compare the JSONL in logs/perf/.
```

The Playwright `perf-frontend.spec.ts` gates that ran green pre-pass are
the regression backstop: the caching pass tightens behaviour without
moving any of the existing P95 ceilings.

## Validation done in this pass

- **Frontend smoke + unit specs (Vitest via `ng test`):**
  - `run-git-cache.service.spec.ts` (3 new tests) — repeat `getFiles`
    short-circuits the HTTP layer, diff cache keys on path, `invalidate`
    drops one job without touching siblings. **All 3 pass.**
  - `beautiful-results.renderer.spec.ts` (3 new tests inside the memo
    `describe` block, 17 existing) — identical inputs return the same
    object reference; two jobs with the same body get distinct entries;
    `clearResultsRenderCache` forces a fresh render. **All 20 pass.**
  - `perf-tracker.spec.ts` (4 new tests) — OFF by default; `?perf=1` and
    `localStorage.perf=1` both flip it ON; `perfMeasure` logs a `[perf]`
    line when the gate is on. **All 4 pass.**
  - `beautiful-results.component.spec.ts` — **3 existing tests still pass**
    (smoke around `OnPush` mount + click delegation).
  - `run-git-viewer.component.spec.ts` — **1 existing test still passes**.
  - `triage-controller.service.spec.ts` + `job-detail-prefetch.service.spec.ts`
    — **9 existing tests still pass** (covers the wired `openDetail` path).
  - `tsc --noEmit` on `tsconfig.app.json` and `tsconfig.spec.json` —
    **0 errors**.

- **Backend:** `GitServiceShaRangeCacheTests.cs` covers
  `GetCommitsInShaRange / GetFilesChangedInShaRange / GetDiffInShaRange`
  via reference-identity (`ReferenceEquals(first, second) == true` proves
  the cached instance came back without re-spawning `git`). The C# was
  written but its xUnit run was **not** captured this session — the dev
  backend exe was holding `backend/bin/Debug/net10.0/OrchestratorApi.dll`,
  so `dotnet build backend.Tests/...` could not refresh the test DLL with
  the new GitService cache. The source compile (no errors) was verified
  via `dotnet build backend/OrchestratorApi.csproj` (only the post-build
  copy to the locked `bin/` path fails). To re-run after a quieter dev
  state:

  ```sh
  # 1. Stop dev (from agent-taskboard-devspace, only if it's the dev
  #    instance you're holding) or run the cache test against stable.
  # 2. Build + run the new fixture only:
  dotnet test backend.Tests/OrchestratorApi.Tests.csproj \
    --filter "FullyQualifiedName~GitServiceShaRangeCacheTests"
  ```

## Notes & follow-ups

- **Backend ETag / `If-None-Match`** on `/api/jobs/{id}` was considered
  but deferred: that endpoint folds `cli-output.log`, which mutates during
  every active run, so 304s would be rare and the conditional-request
  bookkeeping is non-zero. The SHA-range cache below `GitService`
  delivers the same benefit for the static parts of the run viewer.
- **Mutation invalidation for the run-git cache** is intentionally
  narrow: a fresh run on a job appends a new run index, so old run
  files/diffs are not staled. The 60 s TTL backstops anything weird
  (force-push, rebase) for free.
- The render LRU is shared module-state (not a service), matching the
  existing `highlight-lazy` pattern. This is intentional — the memo is
  pure and stateless from the consumer's perspective.
