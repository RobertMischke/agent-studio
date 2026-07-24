# Frontend E2E Tests (Playwright)

End-to-end regression suite for the Agent Software Studio frontend.

## Why this exists

After every change with visual or behavioural impact in `frontend/`, run the
relevant Playwright spec(s) before declaring the task done. Static type checks
and unit tests do not catch UI regressions; this suite does.

## Prerequisites

The tests assume the dev stack is already running:

| Service  | Command                                    | URL                     |
|----------|--------------------------------------------|-------------------------|
| Backend  | `./api.sh start` (from repo root)          | http://localhost:5030   |
| Frontend | `npm start --prefix frontend` (or VS Code task `Frontend: Start`) | http://localhost:4010 |

`playwright.config.ts` does not spawn these — both fail fast if missing.

## Running

All commands are sh / bash. Do not wrap them in PowerShell — agent CLIs hang.

```sh
# from repo root
npm --prefix frontend run e2e            # headless run, all specs
npm --prefix frontend run e2e:ui         # interactive UI mode (debugging)
npm --prefix frontend run e2e:headed     # watch the browser
npm --prefix frontend run e2e -- e2e/cli-usage.spec.ts   # single spec
npm --prefix frontend run e2e:report     # open last HTML report
```

First-time browser install (already done in dev setup, but if the
`chromium-headless-shell` binary is missing):

```sh
npx --prefix frontend playwright install chromium
```

## Suite layout

Specs are grouped by domain under `e2e/<folder>/`, mirroring the feature
folders under `src/app/features/`. Folder picks are heuristic; when adding a
new spec, drop it where its primary surface lives (e.g. anything that opens
the kanban board → `board/`, anything that mounts the orchestrator sidesheet →
`orchestrator/`). The `<folder>` layer is just organisation — Playwright
auto-discovers via `testDir: './e2e'` so no path tweaks are needed.

| Folder | Domain | Approx count |
|--------|--------|--------------|
| `add-task/` | Add-Task dialog flows: open, attachments, prompt enhance, generated titles. | 6 |
| `board/` | Kanban / lanes / cards: lane reorder, archive, backlog tags, auto-pickup, drag-and-drop, lane scroll. | 30 |
| `chat/` | Activity log + chat surfaces + next-gen chat workbench: messages, tool bursts, markdown, Composer host wiring. | 19 |
| `cli/` | CLI back-ends: Claude / Codex / Gemini / Copilot smokes, quota, sessions, CLI-usage hub in the settings home. | 13 |
| `dev-tools/` | Dev mode, stable update pipeline, backend fixture, smoke + refactor baselines. | 7 |
| `git/` | Git pane: diff viewer, tree, commit chain, tooltip overflow, no-NG0600 regression. | 6 |
| `layout/` | VS-Code-layout shell, statusbar, sidebar filter sheet, drag-auto-scroll, header buttons. | 7 |
| `mockups/` | Pure mockup-screenshot specs that pin design references (`docs/mockups/...`). | 5 |
| `orchestrator/` | Orchestrator side sheet + settings + steering + Composer-backed project chat. | 9 |
| `perf/` | Perf baselines + frontend stress. | 3 |
| `project/` | Per-project detail panels: drift, observability, runtime, security, UX/UI, token usage, steering, identity. | 15 |
| `system/` | Cross-cutting chrome: modal stack, caret suppression, markdown body, concept help, decision banner, update banner. | 8 |
| `task-detail/` | Task detail view: header, lane pager, status dropdown, panes, protocol pane, prompts, triage, verbose debug. | 29 |
| `visual-evidence/` | Screenshots reel, lightbox, README + dialog screenshot regressions. | 8 |

Per-spec coverage map is auto-generated below (run
`node scripts/generate-e2e-coverage-map.mjs --write` to refresh).

Tests with `@billable` in the title call real CLIs and consume quota. They are
skipped automatically when `process.env.SKIP_BILLABLE === '1'`.

## Selector conventions

1. `data-testid="..."` — first choice. Add one to the component if missing
   rather than reaching for a fragile selector.
2. ARIA role + accessible name — `getByRole('button', { name: 'Add Task' })`.
3. Visible text — only for content that is part of the user-facing copy and
   stable.

Do **not** select by CSS class names; they belong to styling and change often.

## Helpers

`e2e/helpers/`
- `jobs.ts` — REST helpers for creating, polling and deleting jobs via the
  backend API at port 5030. Use these for setup/teardown to keep tests fast
  and deterministic.
- `quota.ts` — fetches `/api/cli/usage` and asserts the Claude section is
  available and has spare quota.

## Fixtures

`e2e/fixtures/`
- `dev-backend.ts` — Playwright fixture that brings the **dev backend** up
  on port 5030 before a spec runs and tears it down after. Use this when a
  spec runs from stable and needs to drive dev as a regression-test target.
  The fixture calls `scripts/supervisor/dev-lifecycle.sh start` / `stop` and
  is idempotent: if the dev backend was already healthy when the fixture
  loaded, the fixture leaves it alone on teardown. Set
  `KEEP_DEV_ON_FAIL=1` to keep dev up after a failure for inspection. The
  fixture exposes `{ port, baseUrl, workspace }` to the test; resolve the
  workspace path from `DEV_CHECKOUT` env, the backend's `/api/watch-paths`,
  or fall back to the script default — never hard-code the path in a spec.

  ```ts
  import { test, expect } from './fixtures/dev-backend';

  test('something against dev', async ({ devBackend }) => {
    const res = await fetch(`${devBackend.baseUrl}/api/tasks`);
    // ...
  });
  ```

  **Convention:** dev's backend is offline by default. Only Playwright specs
  that need it should bring it up, via this fixture. Specs that just hit the
  same target the user is on (dev or stable) do not need the fixture; use
  the `PW_TARGET` env var instead.

## Authoring guidelines

- One spec = one user-visible feature. Keep specs small.
- No hardcoded waits (`waitForTimeout`). Use `expect.poll` or web-first
  assertions.
- Always clean up jobs you create — leftover jobs pollute the dev board.
- Tag long/expensive specs with `@billable` so CI can opt out.

<!-- COVERAGE-MAP-START - regenerated by scripts/generate-e2e-coverage-map.mjs -->

### `add-task/` - 10 specs

| Spec | Summary |
|------|---------|
| `add-task-attachment.spec.ts` | Add Task dialog - image attachments |
| `add-task-close.spec.ts` | Add Task dialog - explicit close only |
| `add-task-enhance-prompt.spec.ts` | Add Task - Enhance prompt button |
| `add-task-generate-title.spec.ts` | Add Task - Generate title button |
| `add-task-legacy.spec.ts` | Add Task dialog legacy guidance |
| `add-task-mode-picker.spec.ts` | Add Task - mode picker |
| `add-task-parent-epic-picker.spec.ts` | Add Task - parent epic picker |
| `add-task.spec.ts` | Add Task - model selection |
| `create-job-with-screenshot.spec.ts` | Add Task dialog - drop + paste screenshot uploads |
| `promote-planning-to-coding.spec.ts` | Promote planning result -> coding task |

### `board/` - 75 specs

| Spec | Summary |
|------|---------|
| `archive-all-loading.spec.ts` | Archive-all loading indicator |
| `archive-lane.spec.ts` | Archive lane |
| `archive-tooltip.spec.ts` | Archive row layout & tooltip |
| `auto-pickup-screenshots.spec.ts` | Auto-pickup toggle - visual states |
| `auto-pickup-toggle.spec.ts` | Auto-pickup toggle |
| `auto-review-multi-aspect.spec.ts` | Auto-review multi-aspect surface |
| `backlog-lane-and-tags.spec.ts` | Backlog lane + task types + tags |
| `board-hides-epics.spec.ts` | Board hides epics (tasks-only lanes) |
| `board-search-screenshots.spec.ts` | board search - empty + filtered states |
| `board-search.spec.ts` | Board search (header icon) |
| `bug-cross-project-counter.spec.ts` | project chip strip: single-select switch |
| `card-cooldown-retry.spec.ts` | DtC step 6 - CooldownRetry banner on the progress card |
| `card-delete-button.spec.ts` | Card delete button |
| `card-live-state-by-lane.spec.ts` | Task-card live-state visibility by lane |
| `card-merge-signal.spec.ts` | AGT-2046 board card merge signal |
| `card-mode-badge.spec.ts` | Card mode badge (planning / research recognizable on the board) |
| `card-publishable-chip.spec.ts` | PUB-1 · accepted-task publishable chip (mocked) |
| `card-state-pill-matches-lane.spec.ts` | Card running cue follows lane, not stale execution |
| `client-attribution.spec.ts` | Client identity + attribution |
| `collapsed-lane-identity-and-cascade.spec.ts` | Collapsed lane identity + cascade regression |
| `collapsed-lane-rail-rhythm.spec.ts` | Collapsed lane-rail vertical rhythm |
| `collapsed-lane-theme.spec.ts` | Collapsed lane-rail theme regression |
| `cross-lane-drop-position.spec.ts` | Cross-lane drop preserves drop position |
| `dnd-no-flash.spec.ts` | Drag-and-drop motion CSS contract (static harness) |
| `done-decide-escalated-card.spec.ts` | Done & Decide - escalated cards do not look like Done |
| `effective-model-on-card.spec.ts` | job-card effective model |
| `effective-model-screenshots.spec.ts` | effective model screenshots |
| `epic-create-from-empty-state.spec.ts` | Epics empty-state + create dialog (per project) |
| `epic-detail-editable.spec.ts` | Epic detail: editable title + properties (Edit & Status) |
| `epic-detail-rollup-board.spec.ts` | Epic detail: rollup board + in-place sub-task swap |
| `epic-group-board-inline-regression.spec.ts` | Board: epic group inline sub-tasks regression |
| `epic-group-board.spec.ts` | Board: group by epic |
| `epic-overview-history.spec.ts` | Epic overview history |
| `epic-overview-screen.spec.ts` | Epic overview screen |
| `epic-rollup-tight-viewport.spec.ts` | Epic rollup: tight viewport / resize keeps lanes enclosed and reachable |
| `execution-location-badge.spec.ts` | shows each concurrent task owner and limits warnings to the stale remote run |
| `explorer-collapse-screenshots.spec.ts` | F27 visual evidence |
| `explorer-collapse.spec.ts` | F27: Explorer-tree folder headers are all collapsible |
| `f39-running-card-themes.spec.ts` | F39 - running task-card across themes |
| `failed-pickup-lane.spec.ts` | ADR-0051 failed-pickup lane is eliminated |
| `filter-active-badge.spec.ts` | Filter-active badge (F59) |
| `historical-review-chips.spec.ts` | historical reissue and abort chips |
| `info-button-lane-headers.spec.ts` | Info button on lane headers (selective placement) |
| `job-card-polish.spec.ts` | F57 - Board-card polish |
| `kanban-full-width.spec.ts` | Kanban full-width layout |
| `kanban-lane-containers-screenshots.spec.ts` | Lane container visual evidence |
| `kanban-lane-containers.spec.ts` | Kanban container header / focus-expand |
| `kanban-lane-grouping.spec.ts` | Kanban lane grouping and collapse |
| `kanban-lane-overlap.spec.ts` | Kanban lane robustness across widths and during collapse |
| `kanban-lane-scroll-consistency.spec.ts` | Kanban lane scroll model stays consistent across every lane |
| `kanban-ready-lane-width.spec.ts` | Ready lane width parity and lack of horizontal scrollbar |
| `kanban-reorder-drop-on-top.spec.ts` | Kanban lane reorder: drop-on-top must set order=1 |
| `kanban-seven-lanes.spec.ts` | ADR-0025 seven-lane kanban |
| `lane-rename-no-human-prefix.spec.ts` | renders Ready / Review / Post Processing headings and never legacy human or auto-review headings |
| `lane-reorder-default-sort.repro.spec.ts` | REPRO default-sort within-lane reorder |
| `lane-reorder-drag.spec.ts` | Lane drag-and-drop reorder |
| `lane-reorder-drop-on-card.spec.ts` | Within-lane drag-drop never drops the card from the lane |
| `lane-reorder-five-cards.spec.ts` | Within-lane reorder at 5-card density |
| `lane-scrollbar-screenshots.spec.ts` | F28 - board screenshots in both themes |
| `lane-scrollbar.spec.ts` | F28 - lane scrollbar redundancy |
| `lane-status-cluster-screenshots.spec.ts` | Lane status cluster - visual reel |
| `lane-status-cluster-shared-workspace.spec.ts` | Lane status cluster - shared workspace / multi-backend |
| `lane-status-cluster.spec.ts` | Lane status cluster - In-Progress lane |
| `move-locked-diagnosis.spec.ts` | Locked folder move surfaces a clear diagnosis |
| `no-redundant-scrollbars.spec.ts` | F60 - no redundant scrollbars in super-column layout |
| `optimistic-reorder-evidence.spec.ts` | @evidence capture optimistic reorder before/after screenshots |
| `post-processing-lane-identity.spec.ts` | Post Processing lane identity |
| `remote-running-card.spec.ts` | Remote-running card visibility, steer wait, and timeout recovery |
| `signalr-jobs-hub.spec.ts` | SignalR jobs hub - push delivery |
| `stalled-progress-card.spec.ts` | stalled Progress cards and lane subset are visible at a glance |
| `task-filter-removed.spec.ts` | Task filter axis removed from filter list |
| `thinking-level-indicator.spec.ts` | shows the effective level and highlights deviations from the client default in both themes |
| `token-popover-contrast.spec.ts` | Token popover WCAG-AA contrast |
| `token-popover-viewport.spec.ts` | Token popover open/close + viewport (ASS-1700) |
| `tooltip-standard.spec.ts` | canonical tooltip layer is lazy, instant, singleton, and visually shared across surfaces |

### `chat/` - 28 specs

| Spec | Summary |
|------|---------|
| `activity-chat-compose.spec.ts` | Activity tab - chat compose |
| `activity-log-chat-mode.spec.ts` | Activity log - conversation mode |
| `activity-log-copy.spec.ts` | Task log - copy buttons |
| `activity-log-live-status.spec.ts` | Activity log - live status indicator |
| `activity-log-markdown.spec.ts` | Activity log - Conversation markdown rendering |
| `activity-log-tool-chips.spec.ts` | Activity log - tool chips |
| `activity-log-visibility.spec.ts` | Activity log - visibility @billable |
| `activity-plan-toggle.spec.ts` | Activity tab Plan / CLI toggle |
| `activity-tab-no-gap.spec.ts` | Activity tab - no gap between log and compose |
| `chat-attachment-inline-and-lightbox.spec.ts` | Project chat - inline attachment render + lightbox |
| `chat-continue.spec.ts` | Activity tab - interactive chat continuation |
| `chat-embedded-events.spec.ts` | Project chat next-gen semantic events |
| `chat-markdown-collapse.spec.ts` | Project chat markdown - Slice A primitives |
| `chat-sticky-composer.spec.ts` | Chat sticky composer |
| `chat-tool-burst.spec.ts` | @mockup next-gen chat tool-burst chip |
| `chat-visual-iteration.spec.ts` | Chat revamp - visual iteration |
| `continuation-log-accumulation.spec.ts` | Continuation log accumulation |
| `conversation-coalesce-agent-bursts.spec.ts` | Conversation view coalesces consecutive agent bursts |
| `conversation-meta-collapse-and-progressive.spec.ts` | Conversation view collapses meta + progressively discloses items |
| `conversation-view-stick-to-bottom.spec.ts` | Conversation view sticks to the latest entry |
| `layout-review.spec.ts` | Layout review - sweep |
| `layout-zoom.spec.ts` | Layout zoom - premium polish review |
| `next-gen-chat-actor-rails.spec.ts` | @mockup next-gen chat actor rails and decision cards |
| `next-gen-chat-angular-prototype.spec.ts` | @mockup next-gen chat Angular prototype |
| `next-gen-chat-task-host.spec.ts` | Next-gen chat task host adapter (Frontend:NextGenChat) |
| `next-gen-chat-workbench-regression.spec.ts` | @regression next-gen chat workbench |
| `task-detail-simple-chat.spec.ts` | Task detail Activity chat is message-only |
| `token-bubble.spec.ts` | Token bubble on job cards |

### `cli/` - 16 specs

| Spec | Summary |
|------|---------|
| `claude-cross-cli-session.spec.ts` | Claude Code - cross-CLI session handover @billable |
| `claude-hello-world.spec.ts` | Claude Code - hello world @billable |
| `claude-rate-limit-live.spec.ts` | Claude - live rate-limit capture @billable |
| `claude-streaming.spec.ts` | Claude Code - incremental streaming @billable |
| `claude-umlaut.spec.ts` | Claude Code - umlaut prompt with Sonnet 4.6 @billable |
| `cli-admin-caps-legibility.spec.ts` | Usage-caps panel legibility |
| `cli-admin-models-contracts.spec.ts` | Admin/CLI page - models & completion contracts |
| `cli-admin-quota-caps.spec.ts` | CLI Admin / quota caps |
| `cli-config-copilot-only.spec.ts` | CLI configuration card |
| `cli-icons-screenshots.spec.ts` | CLI icons - screenshots @screenshots |
| `cli-icons.spec.ts` | CLI icons - distinct glyph per CLI |
| `cli-skills-pickup.spec.ts` | CLI skills - pickup @billable |
| `cli-usage-project-clickthrough.spec.ts` | clicking a project usage row opens that project Settings rail |
| `cli-usage.spec.ts` | CLI usage hub (status-bar → settings home) |
| `gemini-hello-world.spec.ts` | Gemini - hello world @billable |
| `quota.spec.ts` | Claude quota |

### `dev-tools/` - 9 specs

| Spec | Summary |
|------|---------|
| `_refactor-baseline.spec.ts` | @baseline refactor visual capture |
| `app-vermessung-v1.spec.ts` | App-Vermessung v1 real stable sweep |
| `dev-backend-fixture.spec.ts` | dev-backend fixture |
| `dev-icon-render.spec.ts` | dev favicon decodes to the orange dev SVG |
| `dev-mode-banner.spec.ts` | DEV-mode visual markers |
| `drive-stable.spec.ts` | snapshot: kanban with running job |
| `mini-test.spec.ts` | Mini test - take a screenshot |
| `smoke-stable.spec.ts` | smoke: stable kanban renders and is captured |
| `tag-manager-dialog.spec.ts` | Tag manager dialog |

### `exploratory/` - 2 specs

| Spec | Summary |
|------|---------|
| `todo-app-final-state.spec.ts` | capture final state and render of the produced todo app |
| `todo-app-full-test.spec.ts` | full lifecycle: create → steer → complete (Playwright Test sandbox) |

### `git/` - 8 specs

| Spec | Summary |
|------|---------|
| `commit-tooltip-overflow.spec.ts` | commit-pill tooltip clips long file rows inside the box |
| `git-diff-large.spec.ts` | Git pane - large-diff gutter must not escape the scroll container |
| `git-diff-viewer.spec.ts` | Git pane - diff viewer + maximize |
| `git-pane-preview-and-grouping.spec.ts` | Git pane - preview, path disambiguation, and diff grouping (AGT-2008) |
| `git-pill-and-claude-telemetry.spec.ts` | Board - git pill on tile |
| `git-tree-and-split.spec.ts` | Git pane - tree view and split layout |
| `git-tree-no-ng0600.spec.ts` | git file tree renders without NG0600 (signal write inside computed) |
| `git-view-layout-shots.spec.ts` | AGT-2011 · Git-View layout shots (mocked) |

### `layout/` - 24 specs

| Spec | Summary |
|------|---------|
| `cli-model-selector-parity.spec.ts` | CLI + model selector parity across sites |
| `drag-auto-scroll.spec.ts` | Drag auto-scroll |
| `explorer-count-row-spacing.spec.ts` | Explorer count-row vertical spacing |
| `header-buttons-cleanup-screenshots.spec.ts` | header cleanup - visual snapshot |
| `header-filter-dropdown.spec.ts` | Header filter dropdown |
| `kanban-filter-sidesheet.spec.ts` | Kanban filter panel (activity bar) |
| `orchestrator-inside-shell.spec.ts` | orchestrator rail sits inside studio-shell body |
| `orchestrator-phases-continuous.spec.ts` | orchestrator chat renders no inline phase/super-phase dividers |
| `project-drag-between-workspaces.spec.ts` | Sidebar: drag a project onto a workspace folder |
| `status-bar-and-header.spec.ts` | Status bar and header size |
| `status-bar-codex-percent-quota.spec.ts` | Status bar quota: Codex %-only payload |
| `status-bar-codex-spark-quota.spec.ts` | Status bar quota: Codex Spark windows |
| `status-bar-layout-consolidated.spec.ts` | Status bar layout consolidated |
| `status-bar-layout-spacer.spec.ts` | Status bar layout: dense left quota + right dock |
| `status-bar-menu-migration.spec.ts` | Status bar default picker (unified chip) |
| `status-bar-panel-active-state.spec.ts` | Status bar panel buttons - active/toggle state |
| `status-bar-quota-pills-polish.spec.ts` | AGT-2058 status-bar quota pills polish |
| `status-bar-quota-two-windows.spec.ts` | Status bar quota: uniform primary pill |
| `status-bar-usage-modal.spec.ts` | Status bar usage modal |
| `studio-sidebar-resize.spec.ts` | Studio sidebar resize |
| `tasks-activity-panel-cleanup.spec.ts` | Tasks activity panel - removed |
| `verify-orchestrator-inside-shell.spec.ts` | orchestrator side sheet renders inside studio-shell body grid |
| `vscode-layout-flag.spec.ts` | Frontend:VsCodeLayout flag |
| `workspace-settings-home.spec.ts` | Workspace settings home (Dach) |

### `menu/` - 1 spec

| Spec | Summary |
|------|---------|
| `menu-no-icons.spec.ts` | Menu surfaces are text-only |

### `mockups/` - 9 specs

| Spec | Summary |
|------|---------|
| `chat-window-next-gen-mockup.spec.ts` | @mockup chat-window-next-gen |
| `experimentier-workbench-mockup.spec.ts` | @mockup experimentier-workbench |
| `meta-cycle-mockup-screenshot.spec.ts` | meta-cycle mockup screenshots - overview, last cycle, configuration, banner states |
| `orchestrator-prep-mockup-screenshot.spec.ts` | orchestrator-prep mockup: low-autonomy and fully-auto board states |
| `plan-strip-mockup.spec.ts` | @mockup plan-strip (real component) |
| `project-overview-dashboard-mockup.spec.ts` | Project Overview interactive mockup |
| `result-view-mockup.spec.ts` | @mockup result-view (real component) |
| `task-progress-tracking-mockup.spec.ts` | task-progress-tracking mockup |
| `vscode-layout-mockup.spec.ts` | @mockup vscode-layout |

### `orchestrator/` - 14 specs

| Spec | Summary |
|------|---------|
| `f14-context-chip-and-caching.spec.ts` | F14: context badge, menu and send caching |
| `orchestrator-chat-content-visible.spec.ts` | orchestrator chat - content stays visible after load |
| `orchestrator-config-panel.spec.ts` | Orchestrator logic config (consolidated Settings, Admin/System entry) |
| `orchestrator-context-header.spec.ts` | Orchestrator context header · where am I |
| `orchestrator-feed-overlay.spec.ts` | global orchestrator feed: status bar opens it, filters, layout + contrast hold on both themes |
| `orchestrator-project-chat.spec.ts` | orchestrator project chat |
| `orchestrator-review-subsection.spec.ts` | (no description) |
| `orchestrator-side-sheet-pin.spec.ts` | Orchestrator side sheet · navigation context + pin |
| `orchestrator-side-sheet-position.spec.ts` | Orchestrator side sheet position |
| `orchestrator-side-sheet.spec.ts` | Orchestrator side sheet |
| `orchestrator-steering.spec.ts` | Orchestrator steering |
| `project-chat-bug-directive.spec.ts` | Project chat - Slice E /bug directive |
| `project-chat-context-awareness.spec.ts` | Project chat context awareness |
| `project-chat-fix.spec.ts` | Project chat fix - silent drop, sluggishness, parallel use |

### `perf/` - 3 specs

| Spec | Summary |
|------|---------|
| `perf-baseline.spec.ts` | Frontend perf baseline |
| `perf-frontend.spec.ts` | Frontend perceived latency |
| `perf-stress.spec.ts` | Frontend stress: render perf at scale |

### `project/` - 39 specs

| Spec | Summary |
|------|---------|
| `cli-permission-modes.spec.ts` | admin: CLI modes render with YOLO default + warning banner, toggle reaches the probe |
| `lane-sort-strategy.spec.ts` | workflow: per-lane dropdowns render resolved strategy and persist a change |
| `nav-level-and-prompt-overrides.spec.ts` | prompt overrides are explicit and filterable in both themes |
| `nav-rebuild-t5a.spec.ts` | project rail: Pipeline / Workflow / Prompts shells in Config |
| `nav-rebuild-t5b.spec.ts` | Project Settings no longer hosts the relocated sections |
| `pipeline-cost-timeline.spec.ts` | token usage: pipeline cost-by-step-kind section renders legend + stacked trend |
| `pipeline-drift-steps.spec.ts` | pipeline: drift dimensions appear as opt-in post-steps that default OFF |
| `pipeline-page-evidence-real.spec.ts` | pipeline page (real): reworked panel renders against the live backend |
| `pipeline-page-evidence.spec.ts` | pipeline page: reworked panel shows steps, models, prompt bindings, per-step tokens |
| `pipeline-step-config.spec.ts` | pipeline: pipeline-step section renders and a per-step model change persists |
| `project-analysis-reports.spec.ts` | empty state renders manual triggers and schedule rows |
| `project-cli-modes.spec.ts` | Admin CLI & Modelle toggles Codex to YOLO and the effective-mode probe reloads it |
| `project-docs-section.spec.ts` | Project detail - Security & Architecture sections |
| `project-docs-sections.spec.ts` | Project docs sections |
| `project-drift-architecture-marble.spec.ts` | no architecture model: empty state with explanatory copy |
| `project-drift-overview.spec.ts` | empty state: section visible with action buttons; no scored block |
| `project-execution-assignment.spec.ts` | assigns a remote host and completes the guided readiness probe |
| `project-hub-nav-ia.spec.ts` | default rail shows four collapsible segments with Agent Docs + Prompts in Context |
| `project-identity.spec.ts` | Project identity & running prominence |
| `project-observability-panel.spec.ts` | rail entry opens the observability panel and shows empty state when no bus traffic |
| `project-overview-cli-environment.spec.ts` | overview is operator-only and settings owns CLI environment details |
| `project-overview-dashboard.spec.ts` | Project Overview · operator dashboard |
| `project-product-runtime-panel.spec.ts` | rail entry opens the product runtime panel and shows empty state when no events |
| `project-security-panel.spec.ts` | empty state - no baseline, no reviews, all actions render |
| `project-settings-panel.spec.ts` | settings rail renders the real panel mirroring the global defaults |
| `project-settings-workspace-dropdown.spec.ts` | workspace dropdown lists every watch path with the current one selected |
| `project-shell-rail.spec.ts` | opens the project shell from the kanban tab and lands on Overview |
| `project-steering-docs.spec.ts` | Project detail - Agent Docs section |
| `project-tab-tokens.spec.ts` | Per-project token total badge |
| `project-token-usage-panel.spec.ts` | empty state - no orchestrator entries renders explicit empty copy |
| `project-url-preview-in-place.spec.ts` | keeps start, settings, live output, and stop in the embed in both themes |
| `project-uxui-panel.spec.ts` | empty state - no design folder, all action buttons render |
| `project-wiki-interactive-html.spec.ts` | AGT-2083 exploration runs scripts while parent access stays blocked |
| `project-wiki-section.spec.ts` | Project detail - Knowledge section |
| `proposals-hub.spec.ts` | Project Hub proposals render in both themes |
| `wiki-pulse.spec.ts` | Wiki Pulse landing view (PULSE-2) |
| `workflow-lanes-t6a.spec.ts` | Workflow rail renders lane list, transitions, and stage 2/3 placeholders |
| `workspace-create-and-delete.spec.ts` | create dialog rejects empty + duplicate names client-side |
| `workspace-token-timeline.spec.ts` | Workspace token timeline |

### `settings/` - 6 specs

| Spec | Summary |
|------|---------|
| `appearance-layout-toggles.spec.ts` | Settings - Appearance/Layout segmented toggles |
| `remote-hosts.spec.ts` | Remote Hosts settings section |
| `settings-consolidation.spec.ts` | Settings consolidation (AGT-2035) |
| `task-server.spec.ts` | Task Server settings section |
| `workspace-settings-panel-screenshots.spec.ts` | captures light-theme screenshot of Settings Workspaces section |
| `workspace-settings-panel.spec.ts` | Settings - Workspaces section (F47) |

### `studio-shell/` - 13 specs

| Spec | Summary |
|------|---------|
| `activity-bar-board-removed.spec.ts` | studio-shell · All-projects board opens only via Explorer header |
| `activity-bar-single-active.spec.ts` | studio-shell · Activity Bar marks exactly one active item |
| `explorer-project-board-and-view-links.spec.ts` | Explorer · project links to Board / Project Hub / Wiki / Epics |
| `header-toolbar-polish.spec.ts` | Header toolbar polish |
| `hub-project-wiki-switch.spec.ts` | Project ⇄ Wiki switch with the Hub tab already open (AGT-2023) |
| `navigation-no-deadend.spec.ts` | studio-shell · navigation has no dead end |
| `project-hub-git-view.spec.ts` | Project Hub · Git View (mocked) |
| `project-hub-publish-badges.spec.ts` | PUB-1 · Project Hub publish badges (mocked) |
| `project-urls.spec.ts` | Project URLs · Explorer tree row + Project Hub page |
| `reload-restores-current-view.spec.ts` | studio-shell · reload restores the current view (F5 bug) |
| `tab-hover-status-card-screenshots.spec.ts` | Open-Tabs hover - evidence screenshots |
| `tab-hover-status-card.spec.ts` | Open-Tabs hover → TaskStatusCard popover |
| `tab-label-tooltip-alignment.spec.ts` | restored task tab hides its watch path and centres its key |

### `system/` - 23 specs

| Spec | Summary |
|------|---------|
| `app-markdown-central-component.spec.ts` | <cac-markdown> central component |
| `caret-suppression.spec.ts` | caret suppression on non-text-input elements |
| `concept-help.spec.ts` | orchestrator concept-help on the global orchestrator card |
| `crash-recovery-prompt.spec.ts` | Crash recovery prompt |
| `escape-modal-stack.spec.ts` | Escape modal-stack arbitration |
| `f31-app-markdown-screenshots.spec.ts` | F31: <app-markdown> screenshots |
| `f37-notification-themes.spec.ts` | F37 - unified notification component, light + dark |
| `f40-update-banner-themes.spec.ts` | F40 - banner / toast theme contracts (dark + light) |
| `f41-dev-banner-themes.spec.ts` | F41 - dev banner stays legible in both themes |
| `f56-toast-all-notifications.spec.ts` | F56 - Toast-pattern for all notifications |
| `live-decision-banner.spec.ts` | live decision banner renders with reply affordance |
| `markdown-body-consolidation.spec.ts` | Markdown typography consolidation |
| `probe-mainpage-health.spec.ts` | main page health probe |
| `runner-architecture.spec.ts` | ADR-0044 runner architecture surfaces |
| `stop-no-error-modal.spec.ts` | Stop -> stopped (no error modal) |
| `style-guide-hard-rules.spec.ts` | style-guide hard rules |
| `update-banner.spec.ts` | update surface |
| `update-center-version-truth.spec.ts` | shows running, main and develop truth in ${theme} theme |
| `update-pipeline-ux.spec.ts` | Update Service pipeline UX (mocked) |
| `update-verifier-cold-start-screenshots.spec.ts` | Cold-start verifier toast evidence |
| `update-verifier-cold-start.spec.ts` | Update verifier cold-start toast (mocked) |
| `watchdog-notification-operator-copy.spec.ts` | watchdog Suspicious notification reads in operator-friendly English |
| `workspace-banner-long-message.spec.ts` | workspace banner clamps long auto-review verdict and keeps project below body |

### `task-detail/` - 89 specs

| Spec | Summary |
|------|---------|
| `accept-to-next-task-instant.spec.ts` | Accept-to-next-task is instant |
| `activity-runs-modal.spec.ts` | Activity-pane: compact N Runs chip + modal |
| `agent-work-detail.spec.ts` | Overview Agent Work - grouped tool detail |
| `archive-and-next-on-completed.spec.ts` | (no description) |
| `cli-model-picker-flow.spec.ts` | CLI + model picker flow |
| `code-review-diff-contrast.spec.ts` | F53 - Diff-view contrast (added/removed lines) |
| `codex-activity-log-conversation.spec.ts` | Codex JSONL Activity Log Conversation renders readable agent text and summarized tools |
| `delete-task.spec.ts` | Delete task |
| `detail-chat-first-compression.spec.ts` | Detail page chat-first compression - Slice 1 |
| `detail-compact-and-maximize.spec.ts` | Detail view - compact command bar, pane maximize, collapsible task list |
| `detail-do-next.spec.ts` | Detail view - Do Next |
| `detail-lane-dropdown.spec.ts` | Detail view - lane dropdown (navigation) |
| `detail-panes-and-git.spec.ts` | Detail view - 3-pane layout + Git view |
| `detail-view-lane-pager.spec.ts` | Detail view - lane pager |
| `detail-view-refresh.spec.ts` | Job detail view - F5 / page refresh |
| `detail-view-status-dropdown.spec.ts` | Detail view - status dropdown (discoverability) |
| `epic-membership-banner.spec.ts` | Task-detail epic-membership banner |
| `epic-model-picker-clipping.spec.ts` | Epic detail - model picker is not clipped |
| `escalation-gave-up.spec.ts` | DtC step 6 - GaveUpToHuman escalation reason |
| `escalation-summary.spec.ts` | Escalation summary panel - collapsible + compact |
| `external-lane-change-keeps-task.spec.ts` | External lane change keeps task in view |
| `f30-detail-header-and-tabs.spec.ts` | F30 - Task-detail header + tabs redesign |
| `f48-files-tab.spec.ts` | F48 Files tab - rename + only-prompt + hint |
| `file-source-history.spec.ts` | File source history viewer |
| `flush-panes-visual.spec.ts` | Flush panes - no card chrome |
| `git-pane-merge-status-consolidated.spec.ts` | Task-Review merge-status shown once |
| `git-pane-no-exclude-control.spec.ts` | Git pane - Exclude-commit override removed |
| `git-view-state-aware.spec.ts` | F55: Git View state-aware display |
| `gitview-perf.spec.ts` | GitView performance - drill-in cache + hover cost |
| `gitview-polish.spec.ts` | GitView polish - contrast + collapsible commit-message banner |
| `inspector-tab-default.spec.ts` | Detail inspector - default tab |
| `job-results-html-render.spec.ts` | Beautiful HTML result rendering |
| `lane-badge-equals-pager-total.spec.ts` | Lane badge == pager total (project-scoped Review) |
| `log-overlay-centering.spec.ts` | Log overlay (maximized agent log) - centering |
| `open-failed-task.spec.ts` | open the failed screenshots-in-editors task and capture errors |
| `overview-agent-metrics-fix.spec.ts` | Overview agent-run metrics fix (tokens + cumulative duration) |
| `overview-failure-details.spec.ts` | Overview failure uses human copy and preserves the full raw diagnostic behind details |
| `overview-prompt-popover.spec.ts` | Overview tab - task prompt modal |
| `overview-tab-is-default-on-task-switch.spec.ts` | Overview tab is the default on task open + switch |
| `overview-tab-model-change.spec.ts` | Overview tab - model picker |
| `overview-tab-title-prominent.spec.ts` | Overview tab - prominent task title at top |
| `overview-tokens-and-session.spec.ts` | Overview tab - tokens fallback + session row removed |
| `pane-header-unified.spec.ts` | F38: unified pane-header tab strip across prompt + protocol |
| `pane-tabs-states.spec.ts` | F38: pane-tab indicator states |
| `pipeline-agent-run-count.spec.ts` | Pipeline Agent-execution run count + details popover |
| `pipeline-column-headers.spec.ts` | Pipeline: per-step metric column headers |
| `pipeline-core-token-usage.spec.ts` | task detail pipeline shows CORE CLI-footer usage, SUM footer, and API-price disclaimer |
| `pipeline-final-verdict-and-parallel.spec.ts` | Pipeline: parallel aspects + orchestrator final verdict |
| `pipeline-live-step-status.spec.ts` | Pipeline live step status |
| `pipeline-loop-guard.spec.ts` | Pipeline loop guard (Ralph-loop early detection) |
| `pipeline-orchestrator-review-distinct.spec.ts` | Pipeline: orchestrator-review rows are distinct, single final verdict |
| `pipeline-prerun-model.spec.ts` | Pipeline: pre-run resolved model |
| `pipeline-regression-radar.spec.ts` | Regression radar pipeline step |
| `pipeline-restart-indicator.spec.ts` | Pipeline restart indicator |
| `pipeline-step-explanations.spec.ts` | Pipeline: per-step explanation tooltips |
| `pipeline-step-usage.spec.ts` | token usage: each pipeline step surfaces its own usage, without the aggregate model block |
| `pipeline-workbench-states-evidence.spec.ts` | Pipeline workbench state evidence |
| `prompt-charset.spec.ts` | Task prompt charset rendering |
| `prompt-edit-lock.spec.ts` | Prompt editor - lock semantics |
| `prompt-pane-header-polish.spec.ts` | F52: prompt-pane sub-header padding, title wrap, meta-row polish |
| `prompt-save.spec.ts` | Prompt editor - Ctrl+S save & visual feedback |
| `protocol-image-flow.spec.ts` | Protocol image flow |
| `protocol-no-screenshot-strip.spec.ts` | F38: protocol pane no longer renders the screenshot strip |
| `protocol-pane-chrome-cleanup.spec.ts` | Protocol pane chrome cleanup (F54) |
| `protocol-pane-cool.spec.ts` | Protocol pane - cool header + pill toggle |
| `protocol-pane-no-pills.spec.ts` | Protocol pane - no Rendered/Raw pills (F63) |
| `protocol-summary-failure-banner.spec.ts` | Protocol pane - summary failure banner |
| `protocol-verdict-and-interim.spec.ts` | Protocol pane - verdict chip + interim status |
| `raw-text-themes.spec.ts` | F32 - Raw-text viewer stays readable across themes |
| `references-in-overview.spec.ts` | Cross-references - compact inside Overview |
| `regression-radar-info-button.spec.ts` | Regression radar info-button |
| `repository-hygiene-strip.spec.ts` | Repository hygiene - review/completed strip |
| `review-evidence-panel.spec.ts` | Review evidence panel |
| `review-evidence-thumbnails.spec.ts` | AGT-1992: review-evidence image thumbnails |
| `row-spacing-compact-density-capture.spec.ts` | Row density - overview screenshots |
| `row-spacing-compact-density.spec.ts` | Row spacing compact density |
| `run-context-expander.spec.ts` | Run timeline: per-run passed context |
| `runtime-console-capture.spec.ts` | runtime console capture |
| `session-events.spec.ts` | Detail - session events + recovery continue |
| `session-task-link-chip.spec.ts` | session->task chip transitions active -> linked and routes click |
| `task-completion-loop-timeline.spec.ts` | Task completion loop - Overview indicator + Timeline tab |
| `task-detail-layout-polish.spec.ts` | Task detail layout polish |
| `task-detail-multi-commit.spec.ts` | Task-detail multi-commit chain |
| `task-detail-no-repo-level-hygiene.spec.ts` | Task detail page does not surface repo-level hygiene signals |
| `task-detail-worktree-isolation.spec.ts` | Task-detail worktree isolation |
| `triage-actions-in-detail-header.spec.ts` | Triage actions in detail header |
| `triage-merge-status.spec.ts` | Human Review acceptance primary is landed-state aware |
| `undo-state-move.spec.ts` | Detail header - state-change undo toast |
| `verbose-debug-overlay.spec.ts` | Verbose Debug overlay - task workbench |

### `visual-evidence/` - 14 specs

| Spec | Summary |
|------|---------|
| `app-survey-report.spec.ts` | app survey filters, area jumps, and card references remain browsable |
| `demo-screenshot-tour.spec.ts` | screenshot tour - kanban board (multi-lane) |
| `f18-diff-light-theme.spec.ts` | F18 - diff token wiring |
| `f20-diff-syntax-highlighting.spec.ts` | F20 - diff body syntax highlighting |
| `f23-shared-menu.spec.ts` | F23 shared <app-menu> migrations |
| `job-screenshots-in-protocol.spec.ts` | Job artifacts harvesting (images-and-protocol) |
| `presentation-capture.spec.ts` | presentation still 01 - cross-lane board |
| `prompt-screenshot-attachment.spec.ts` | Prompt editor - screenshot attachments |
| `readme-screenshots.spec.ts` | readme screenshots - board and task detail states |
| `session-chip-screenshot.spec.ts` | session chip - visual capture (continued / lost / fresh) |
| `task-description-image-lightbox.spec.ts` | Task description image lightbox |
| `undo-toast-bottom-right.spec.ts` | Move/Undo toast docks bottom-right; top-right corner stays free |
| `unified-dialog-screenshots.spec.ts` | Unified confirm + notify visuals |
| `visual-evidence-strip-and-reel.spec.ts` | Visual evidence: per-task strip + lightbox + workspace reel |

### `workspace/` - 6 specs

| Spec | Summary |
|------|---------|
| `explorer-micro-dashboard.spec.ts` | numbers default, dots toggle, cap, order, a11y, and both themes |
| `explorer-tree-nesting.spec.ts` | AGT-2057: Explorer tree destination nesting |
| `project-board-lane-counters.spec.ts` | Explorer Project Board row shows subtle live lane counters |
| `project-onboarding-basics.spec.ts` | project onboarding, store separation, validation, and editable Project Basics |
| `workspace-rename.spec.ts` | F46: workspace-header inline rename |
| `workspace-tree.spec.ts` | F46: Explorer two-level workspace -> project tree |

<!-- COVERAGE-MAP-END -->
