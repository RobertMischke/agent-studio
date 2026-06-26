# ASS-1727 — Archive view empty despite 852 archived tasks

## Outcome

The core fix was first implemented on this branch in
`6edd9aa7 feat(archive): lazy-load archived tasks from dedicated paged endpoint`
(909 insertions / 74 deletions across the 13 files listed under "Implementation
map" below). That commit was buried behind later, unrelated commits, so the
re-issued review only saw an evidence-only tip commit. This run brings the
feature surface back into the change set **and** resolves the concrete review
findings with real code, not just screenshots:

| Review finding | Resolution (this run) | File |
|---|---|---|
| Redundant archive-building left in place (additive-only diff) | `grouped.archive` now emits `Array.Empty<TaskInfo>()` instead of the misleading `SortLane(7-archive)` call that only ever returned `[]` | `backend/Features/Tasks/TaskCrudEndpoints.cs` |
| Search filter shows no query logs | Search now emits the stable event `task-archive-search` (`term`, `matched`, `archivedScanned`) so a "filter found nothing" report is diagnosable from the api log | `backend/Features/Tasks/TaskCrudEndpoints.cs` |
| New endpoint not documented in load-bearing docs | `GET /api/tasks/archive` fully documented (params, ordering, response shape, log event) in the job-api skill's endpoint reference | `.agents/skills/job-api/references/endpoints.md` |
| Test additions not clear in the diff | 3 new backend tests + 1 new frontend test added in this run's diff (see "Automated test evidence"); they are **new regression tests**, not pre-existing ones | `backend.Tests/TaskArchiveEndpointTests.cs`, `…/task-column.spec.ts` |

## Implementation map (commit `6edd9aa7`)

The full feature lives across these files (read them for the complete change;
this run touches the endpoint, its tests, the FE spec, and the API docs):

- `backend/Features/Tasks/TaskCrudEndpoints.cs` — the paged `/archive` handler.
- `backend/Features/Tasks/TaskScannerService.cs` — `ScanArchivedJobs()` (slim archive read).
- `backend/Features/Tasks/TaskIndexCache.cs` — archive partition in the single shared scan.
- `backend/Shared/Models/TaskInfo.cs` — `ArchivedTaskInfo` / `ArchivedTasksResponse`.
- `frontend/.../task-column/task-column.ts|.html|.scss` — the lazy-load Archive lane.
- `frontend/src/app/services/task.service.ts`, `…/models/task.model.ts` — `getArchivedTasks` + types.
- Tests: `backend.Tests/TaskArchiveEndpointTests.cs`, `TaskIndexCacheTests.cs`, `…/task-column.spec.ts`, `…/task.service.spec.ts`.

## Root cause & fix shape

The default board snapshot (`/api/tasks/grouped`) is served from a cached,
slim-hydrated scan that **deliberately excludes the terminal `7-archive`
lane** so the common board path never pays for scanning a large archive
(see `backend/Features/Tasks/TaskScannerService.cs` and the doc comment on
`GroupedJobs.archive` in `backend/Shared/Models/TaskInfo.cs`). That is by
design — `grouped.archive` stays empty. Before the fix the Archive lane bound
only to that empty array, so it always rendered empty even though hundreds of
archived tasks existed on disk.

The fix keeps the cheap default path intact and adds a dedicated, paged
read endpoint plus a lazy-loading Archive lane:

- **Backend** — new `GET /api/tasks/archive?watchPath=&offset=&limit=&search=`
  returning slim `ArchivedTaskInfo` rows + an honest unfiltered `total`
  (`backend/Features/Tasks/TaskCrudEndpoints.cs`). No existing endpoint was
  renamed.
- **Frontend** — the Archive column (`app-job-column`,
  `frontend/src/app/features/board/components/task-column/task-column.ts`)
  lazy-loads page 1 on init, pages via "Load more", debounces a text filter
  (300 ms, re-queries from offset 0), and shows the empty state **only** once a
  fetch resolves with a genuine `total === 0` (`archiveIsEmpty` guards on
  `archiveLoaded`).

## Automated test evidence (canonical correctness)

- **Backend — 8/8 pass** (`backend.Tests/TaskArchiveEndpointTests.cs`,
  real backend via `WebApplicationFactory<Program>`). The first five existed in
  `6edd9aa7`; the last three are **new in this run**:
  - `Archive_ReturnsArchivedTasks_NewestFirst`
  - `Archive_Paging_SlicesStableOrder_TotalStaysFull`
  - `Archive_Search_FiltersByTitle`
  - `Archive_HidesFixtures_ByDefault_OptInWithIncludeFixtures`
  - `GroupedBoard_StillExcludesArchive_EvenWithArchivedFoldersOnDisk`
  - `Archive_Search_NoMatch_ReturnsEmptyItems_WithZeroTotal` *(new)*
  - `Archive_Limit_IsClampedToBounds` *(new)*
  - `Archive_OffsetBeyondTotal_ReturnsEmptyItems_KeepsFullTotal` *(new)*
- **Frontend** — the archive-lane render spec
  `frontend/src/app/features/board/components/task-column/task-column.spec.ts`
  passes (`19/19`, `ng test --include=…/task-column.spec.ts`), including a
  **new** test this run added:
  `filtered empty state names the filter, not a bare "no archived tasks"`.

## Build & full-suite status (this run)

- `dotnet build backend/OrchestratorApi.csproj` → **Build succeeded, 0 errors**.
- Archive feature tests (backend `TaskArchiveEndpointTests` 8/8 + the FE spec)
  are **green**.
- The full backend suite (`3216` tests) reports **9 pre-existing failures** in
  areas this task does not touch: `MergeEndpointsIntegrationTests` (git/merge
  integration), `ProjectTokenUsageEndpointPerfTests` (timing under parallel
  load), `CodePatternDriftAnalysisServiceTests.Analyze_AgainstLiveDevCheckout`
  (reads the external shared dev checkout), `ProjectChatMigrationTests`, and
  `TaskFolderAccessIsolationTest`. These were **verified pre-existing**: stashing
  this run's changes and re-running the same set reproduces the identical
  failures on the pristine `HEAD` baseline, so they are environment/timing
  flakes independent of the archive work, not a regression introduced here.

## UI-acceptance evidence (`--mocked`)

Captured against **this worktree's production build** (served by
`results/_static-server.mjs`) with the board boot API surface route-mocked by
`results/_archive-shot.mjs` (Playwright). The `/api/tasks/archive` mock honours
`offset` / `limit` / `search` exactly like the real handler; the unfiltered
`total` is reported as **852** to mirror the bug's data shape.

These are **`--mocked`** shots. A `--real` shot is not reachable from a dev job
worktree: the running dev stack (`:4010` / `:5030`) serves the canonical dev
checkout, not this branch, and `AGENTS.md` forbids bringing the dev backend up
from a job. Real-backend correctness is covered by the 5 endpoint tests above.

| State | Evidence | What it proves |
|-------|----------|----------------|
| Populated | ![populated](archive-lane-populated--mocked.png) | Archive lane hydrates from the paged endpoint — count **852**, page-1 rows, "Load more (836 remaining)". The bug (empty lane) is gone. |
| Filtered | ![filtered](archive-lane-filtered--mocked.png) | Typing `migration` re-queries from offset 0 → count **3**, exactly the three matching rows. |
| Empty | ![empty](archive-lane-empty--mocked.png) | A no-match filter (`zzz-no-such-task`) yields a genuine `total === 0` → "No archived tasks match the filter". The empty state is shown **only** when truly empty, never over real data. |

### Notes on the harness

- The Playwright context blocks the app's service worker
  (`ngsw-worker.js`, enabled in the production build). Once active it intercepts
  `/api/**` from its own cache and bypasses the route mock, which would leave the
  lane on stale data; blocking it keeps every request flowing through the mock.
- The SignalR hub (`/hubs/jobs`) is aborted — push isn't part of this evidence
  (the lane hydrates over plain HTTP), so the resulting "failed to connect"
  console lines are expected and benign.
