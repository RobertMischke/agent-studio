# Frontend architecture review — 2026-05-09

This is a maintainability snapshot of the Angular front-end taken during
Cycle 7 of the perf overhaul. It exists because the perf work uncovered
that the *runtime* problems were almost always co-located with
*structural* ones: the components that struggle under load are the
components that have grown past the point one human can hold in their
head. Fixing perf bought us 60 FPS at 2000 chat lines; the architecture
work below buys us the next twelve months of changes without the codebase
getting harder to keep correct.

The review has three deliverables:

1. **A target shape** for what "healthy" looks like in this code base
   (component size, where state lives, how panels get their data).
2. **A ranked list of mega-components** that should be split, with the
   specific extraction targets per file.
3. **An ADR** ([ADR-0034](../../../system/architecture/decisions/adr-archive.md#adr-0034)) documenting
   the doctrine so future work doesn't drift back.

The ADR is the load-bearing piece. The numbered list below is a working
plan that future PRs can pick from.

## What "healthy" looks like

| Surface | Target |
|---|---|
| Single-file Angular component | ≤ 600 LOC. > 1000 LOC is a code smell, > 2000 LOC is a "must split" backlog item. |
| Single-file model / interface file | ≤ 1500 LOC, only types + small helpers. Behaviour belongs in services. |
| Service file | ≤ 600 LOC. A service that grows past that is doing too many things — extract a sibling service. |
| Long-lived state (filters, selected job, viewport mode, draft buffers) | Lives in injectable services with signal exposure, NOT in the root component. |
| Per-panel API calls | Live in the panel's own service (the per-job-detail pattern is the reference: `cli-output-poll.service`, `git-pane.service`, etc.). |
| Cross-panel state | Lives in `providedIn: 'root'` services that emit signals. |
| Polling | Always `setVisibleInterval` from `utils/visible-interval.ts`, never bare `setInterval`. (Hard rule, [docs/quality/frontend/performance.md](../performance.md).) |

Two tests that should both pass: (a) **a new contributor can find the
state for any feature in the directory tree without grepping**, and
(b) **a feature change is one PR with a small diff**, not a rewrite of a
3000-line god-component.

## Snapshot of the largest components (2026-05-09)

```
4734  frontend/src/app/app.ts                              <-- way over budget
2449  frontend/src/app/components/job-detail.ts            <-- way over budget
1486  frontend/src/app/components/activity-log-view.ts     <-- over budget
1320  frontend/src/app/components/orchestrator-side-sheet/orchestrator-side-sheet.component.ts
1241  frontend/src/app/components/verbose-debug/verbose-debug-overlay.component.ts
1215  frontend/src/app/components/job-card.ts
1183  frontend/src/app/components/project-observability/project-observability-panel.component.ts
1180  frontend/src/app/models/job.model.ts                 <-- types-only, OK
1171  frontend/src/app/components/project-drift-overview-section.ts
1130  frontend/src/app/components/chat/chat.component.ts
1109  frontend/src/app/components/job-column.ts
1089  frontend/src/app/components/project-product-runtime/project-product-runtime-panel.component.ts
 994  frontend/src/app/services/job.service.ts             <-- borderline
```

`app.ts` alone holds 170 fields/methods and 32 signal definitions. The
shell is doing the work of at least four services.

## Ranked extraction plan

### Tier 1: load-bearing splits

#### `app.ts` (4734 LOC) — split into shell + four services

The shell should stay; the **state and behaviour around it** moves out.

- **`BoardFiltersService`** (`providedIn: 'root'`).
  Hosts: `activeProjects`, `activeClientFilter`, `activeTypeFilter`,
  `activeTagFilter`, `searchQuery`, plus the `filteredGrouped` /
  `filteredJobCount` / `hasActiveFilters` computeds and all the
  `togglePill` mutations. Persisted slice (today: localStorage for
  `activeProjects` / `collapsedLanes`) moves into the service and stays
  invisible to the shell.

- **`SelectedJobService`** (`providedIn: 'root'`).
  Hosts: `selectedJob`, `openDetailToken`, `triageLaneState`, the
  `openDetail` / `closeDetail` / next-peer / prev-peer flow, and the URL
  `?job=&watchPath=` hydrate path. The shell binds to `selected.detail()`
  and forwards UI events; everything else is in the service.

- **`LaneCollapseService`** (`providedIn: 'root'`).
  Hosts: `collapsedLanes` + `collapsedContainers` + the
  `isLaneCollapsed` / `expandedLaneCount` / `containerSummary` family,
  plus the localStorage persistence. These are pure state-machines today
  (a Set + a few derived values); the service shape is one signal +
  three computeds + one mutator.

- **`OrchestratorChatComposerService`** (`providedIn: 'root'`).
  Hosts: every "compose this orchestrator chat" path (drafts, send,
  attachments, error retries) that currently sits inline in the shell.
  The shell only subscribes to render and emits send-clicks.

After the four services land, `app.ts` should be a few hundred lines of
template + a small adapter that wires the services into the existing
template bindings (mostly `service.signal()` reads). Estimate: 4734 → ~800.

#### `job-detail.ts` (2449 LOC) — already half-done; finish

The detail panel started at one mega-component and has been split over
the last quarter into:

- `cli-output-poll.service`, `run-timeline-poll.service`,
  `session-events-poll.service`, `screenshots-poll.service`,
  `claude-session-poll.service`, `git-pane.service` (per-detail-instance)
- `protocol-pane.component`, `git-pane.component`,
  `prompt-pane.component`, `command-deck.component`,
  `detail-header.component`, `cli-config-card.component`,
  `pane-toggle-bar.component`, `triage-panel.component`,
  `log-overlay.component`, `hygiene-strip.component`

What still lives in `job-detail.ts` and should leave:

- **`PanesLayoutService` extension.** `LayoutPanesService` already exists;
  the maximised-pane state, full-screen toggle, restore-from-storage
  and visibility handling for ~12 layout signals can move there
  entirely. Today the component still owns ~30 layout fields.
- **`CliMetadataService`** (`providedIn: 'job-detail'`). Holds the
  CLI-config draft state (`modelDraft`, `cliTypeDraft`,
  `useOwnSessionDraft`, `applyMetadata`, error/save handlers). Currently
  a mini-form scattered across the shell.
- **`JobActionsService`** (`providedIn: 'job-detail'`). The "Start /
  Continue / Stop / Move" event handlers belong together. Today they
  pierce into JobService, GitService, error dialogs from the component.

Estimate: 2449 → ~600.

#### `activity-log-view.ts` (1486 LOC) — split parser usage from rendering

Cycle 7i added virtualization + memoization; the next step is content
split:

- **`activity-log-toolbar.component`** (header, mode tabs, "show tools" /
  "show debug" toggles, copy button) — already partly extracted via
  `activity-log-search.component`, finish for the rest of the toolbar.
- **`activity-log-conversation.component`** (the `@if (mode() ===
  'conversation')` branch + the cdk-virtual-scroll-viewport from
  Cycle 7i).
- **`activity-log-trace.component`** (the `@else` branch).

The parent stays as a thin coordinator that switches between the two
mode children. Estimate: 1486 → 400 + 400 + 300.

### Tier 2: panel-by-panel cleanups

These each have a clear single responsibility but accreted over time.
Same pattern: extract a per-panel service for state, split sub-sections
into child components.

- **`orchestrator-side-sheet` (1320)** → split panes (boot prompt /
  reply / chat / events) into siblings; lift the chat composer +
  rate-limit subscription into a service.
- **`verbose-debug-overlay` (1241)** → split per-tab; the overlay is
  doing five things in one shell.
- **`job-card` (1215)** → extract chip rendering (token bubble, autoloop
  badge, summary state, owner chip, commit chip, type chip, tag chips)
  into composable child components. The card's own logic is small once
  the chips leave.
- **`project-observability-panel` (1183)**, **`project-drift-overview`
  (1171)**, **`project-product-runtime-panel` (1089)** → each has a
  per-panel service available; finish migrating the remaining state.
- **`chat.component` (1130)** → input area, message rail, and the
  scroll-management code each want to be siblings. The scroll
  management already has a hint at virtualization (`project-chat-list`)
  that can be reused.
- **`job-column` (1109)** → archive-row / review-grouping branches each
  deserve their own component; the column shell is small once they leave.

### Tier 3: services with too much surface

- **`job.service.ts` (994)** is right at the borderline. It already
  splits naturally into "board / detail / runner / git / cli / quota /
  orchestrator" sub-services; consider promoting at least the runner
  and cli surfaces to siblings (`runner-api.service`, `cli-api.service`)
  so the file is a thin façade.
- **`activity-log.parser.ts` (857)** is types + pure functions and is
  fine as one file, but the conversation-grouping logic could live in
  its own helper to make room for future per-CLI parser variants
  (Codex, Copilot, Gemini are known to write differently-shaped
  output).

## Suggested operating model going forward

1. **One PR per Tier 1 split.** Each Tier 1 service extraction is
   meant to land as a single PR with a clear before/after diff:
   `git mv` the helpers into the new service, replace the inline
   bodies with `inject(...)`, run the relevant Playwright spec, ship.
2. **No new fields in the four mega-components.** Future feature work
   that would naturally add signals to `app.ts` or `job-detail.ts`
   instead lands in the appropriate Tier 1 service. A small lint /
   PR review bar is enough; CI doesn't need to enforce it yet.
3. **Run `frontend/e2e/perf-stress.spec.ts` after every Tier 1 PR.**
   Service extraction shouldn't move the perf numbers (the runtime
   work doesn't change), but a regression here is a strong signal that
   change detection got broken on the way out. The spec at
   `RUN_PERF_BASELINE=1 ... npx playwright test e2e/perf-stress.spec.ts`
   takes ~2 minutes and is the cheapest gate we have.

## Specifically out of scope for this review

- A wholesale state-management library (NgRx, signals-store etc.).
  The codebase already uses signals + injectable services well; adding
  a library would be net negative until we have multiple bounded
  contexts that need to share derived state, which we don't.
- Module boundaries via `nx` or library projects. Today the app is one
  Angular project; the size justifies splits but not a multi-project
  workspace, which would slow iteration without a corresponding payoff.
- Test reorganisation. The Playwright suites already follow the surface
  shape; reorganising them on top of a service split is its own PR.

## Reference

- [docs/system/architecture/decisions/adr-archive.md ADR-0033](../../../system/architecture/decisions/adr-archive.md#adr-0033) (runtime-not-bundle perf budget; established 2026-05-09)
- [docs/system/architecture/decisions/adr-archive.md ADR-0034](../../../system/architecture/decisions/adr-archive.md#adr-0034) (component-size + service-extraction doctrine; established 2026-05-09)
- [docs/quality/frontend/performance.md](../performance.md) (frontend perf playbook; the polling and bounded-buffer rules referenced above)
- Per-cycle perf evidence under [`logs/perf/`](../../../logs/perf) and the
  workspace mirror at
  `agent-taskboard-workspace/logs/analysis/_workspace/`.
