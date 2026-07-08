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
| `visual-evidence/` | Screenshots reel, lightbox, README + dialog screenshot regressions. | 7 |

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

<!-- COVERAGE-MAP-START — regenerated by scripts/generate-e2e-coverage-map.mjs -->

### `add-task/` — 6 specs

| Spec | Summary |
|------|---------|
| `add-task-attachment.spec.ts` | Add Task dialog — image attachments |
| `add-task-close.spec.ts` | Add Task dialog — explicit close only |
| `add-task-enhance-prompt.spec.ts` | Add Task - Enhance prompt button |
| `add-task-generate-title.spec.ts` | Add Task — Generate title button |
| `add-task.spec.ts` | Add Task — model selection |
| `create-job-with-screenshot.spec.ts` | Add Task dialog - drop + paste screenshot uploads |

### `board/` — 30 specs

| Spec | Summary |
|------|---------|
| `archive-all-loading.spec.ts` | Archive-all loading indicator |
| `archive-lane.spec.ts` | Archive lane |
| `archive-tooltip.spec.ts` | Archive row layout & tooltip |
| `auto-pickup-screenshots.spec.ts` | Auto-pickup toggle — visual states |
| `auto-pickup-toggle.spec.ts` | Auto-pickup toggle |
| `auto-review-multi-aspect.spec.ts` | Auto-review multi-aspect surface |
| `backlog-lane-and-tags.spec.ts` | Backlog lane + task types + tags |
| `board-search-screenshots.spec.ts` | board search — empty + filtered states |
| `board-search.spec.ts` | Board search (header icon) |
| `bug-cross-project-counter.spec.ts` | project chip strip: single-select switch |
| `card-live-state-by-lane.spec.ts` | Task-card live-state visibility by lane |
| `client-attribution.spec.ts` | Client identity + attribution |
| `compact-cards-toggle.spec.ts` | Compact cards toggle |
| `cross-lane-drop-position.spec.ts` | Cross-lane drop preserves drop position |
| `dnd-no-flash.spec.ts` | Drag-and-drop motion CSS contract (static harness) |
| `failed-pickup-lane.spec.ts` | ADR-0051 failed-pickup lane is eliminated (no lane, banner, or dot) |
| `info-button-lane-headers.spec.ts` | Info button on lane headers (selective placement) |
| `kanban-full-width.spec.ts` | Kanban full-width layout |
| `kanban-lane-containers-screenshots.spec.ts` | Lane container visual evidence |
| `kanban-lane-containers.spec.ts` | Kanban container header / focus-expand |
| `kanban-lane-grouping.spec.ts` | Kanban lane grouping and collapse |
| `kanban-lane-overlap.spec.ts` | Kanban lane robustness across widths and during collapse |
| `kanban-lane-scroll-consistency.spec.ts` | Kanban lane scroll model is consistent across every lane |
| `kanban-ready-lane-width.spec.ts` | Ready lane width parity and lack of horizontal scrollbar |
| `kanban-reorder-drop-on-top.spec.ts` | Kanban lane reorder: drop-on-top must set order=1 |
| `kanban-seven-lanes.spec.ts` | ADR-0025 seven-lane kanban |
| `lane-reorder-drag.spec.ts` | Lane drag-and-drop reorder |
| `lane-reorder-drop-on-card.spec.ts` | Within-lane drag-drop never drops the card from the lane |
| `lane-reorder-five-cards.spec.ts` | Within-lane reorder at 5-card density |
| `optimistic-reorder-evidence.spec.ts` | @evidence capture optimistic reorder before/after screenshots |

### `chat/` — 21 specs

| Spec | Summary |
|------|---------|
| `activity-chat-compose.spec.ts` | Activity tab — chat compose |
| `activity-log-chat-mode.spec.ts` | Activity log — conversation mode |
| `activity-log-copy.spec.ts` | Task log — copy buttons |
| `activity-log-live-status.spec.ts` | Activity log - live status indicator |
| `activity-log-markdown.spec.ts` | Activity log — Conversation markdown rendering |
| `activity-log-tool-chips.spec.ts` | Activity log — tool chips |
| `activity-log-visibility.spec.ts` | Activity log — visibility @billable |
| `activity-tab-no-gap.spec.ts` | Activity tab — no gap between log and compose |
| `chat-attachment-inline-and-lightbox.spec.ts` | Project chat — inline attachment render + lightbox |
| `chat-continue.spec.ts` | Activity tab — interactive chat continuation |
| `chat-embedded-events.spec.ts` | Project chat — Slice B embedded events |
| `chat-markdown-collapse.spec.ts` | Project chat markdown — Slice A primitives |
| `chat-tool-burst.spec.ts` | @mockup next-gen chat tool-burst chip |
| `continuation-log-accumulation.spec.ts` | Continuation log accumulation |
| `next-gen-chat-actor-rails.spec.ts` | @mockup next-gen chat actor rails and decision cards |
| `next-gen-chat-angular-prototype.spec.ts` | @mockup next-gen chat Angular prototype |
| `next-gen-chat-task-host.spec.ts` | Next-gen chat task host adapter (Frontend:NextGenChat) |
| `next-gen-chat-workbench-regression.spec.ts` | @regression next-gen chat workbench |
| `token-bubble.spec.ts` | Token bubble on job cards |

### `cli/` — 13 specs

| Spec | Summary |
|------|---------|
| `claude-cross-cli-session.spec.ts` | Claude Code — cross-CLI session handover @billable |
| `claude-hello-world.spec.ts` | Claude Code — hello world @billable |
| `claude-rate-limit-live.spec.ts` | Claude — live rate-limit capture @billable |
| `claude-streaming.spec.ts` | Claude Code — incremental streaming @billable |
| `claude-umlaut.spec.ts` | Claude Code — umlaut prompt with Sonnet 4.6 @billable |
| `cli-admin-quota-caps.spec.ts` | CLI Admin / quota caps |
| `cli-config-copilot-only.spec.ts` | CLI configuration card |
| `cli-icons-screenshots.spec.ts` | CLI icons — screenshots @screenshots |
| `cli-icons.spec.ts` | CLI icons — distinct glyph per CLI |
| `cli-skills-pickup.spec.ts` | CLI skills — pickup @billable |
| `cli-usage.spec.ts` | CLI-usage hub (status-bar → settings home) |
| `gemini-hello-world.spec.ts` | Gemini — hello world @billable |
| `quota.spec.ts` | Claude quota |

### `dev-tools/` — 7 specs

| Spec | Summary |
|------|---------|
| `_refactor-baseline.spec.ts` | @baseline refactor visual capture |
| `dev-backend-fixture.spec.ts` | dev-backend fixture |
| `dev-icon-render.spec.ts` | dev favicon decodes to the orange dev SVG |
| `dev-mode-banner.spec.ts` | DEV-mode visual markers |
| `drive-stable.spec.ts` | snapshot: kanban with running job |
| `mini-test.spec.ts` | Mini test - take a screenshot |
| `smoke-stable.spec.ts` | smoke: stable kanban renders and is captured |

### `git/` — 6 specs

| Spec | Summary |
|------|---------|
| `commit-tooltip-overflow.spec.ts` | commit-pill tooltip clips long file rows inside the box |
| `git-diff-large.spec.ts` | Git pane — large-diff gutter must not escape the scroll container |
| `git-diff-viewer.spec.ts` | Git pane — diff viewer + maximize |
| `git-pill-and-claude-telemetry.spec.ts` | Board — git pill on tile |
| `git-tree-and-split.spec.ts` | Git pane — tree view and split layout |
| `git-tree-no-ng0600.spec.ts` | git file tree renders without NG0600 (signal write inside computed) |

### `layout/` — 7 specs

| Spec | Summary |
|------|---------|
| `drag-auto-scroll.spec.ts` | Drag auto-scroll |
| `header-buttons-cleanup-screenshots.spec.ts` | header cleanup — visual snapshot |
| `header-filter-dropdown.spec.ts` | Header filter dropdown |
| `kanban-filter-sidesheet.spec.ts` | Kanban filter sidesheet |
| `status-bar-and-header.spec.ts` | Status bar and header size |
| `status-bar-usage-modal.spec.ts` | Status bar usage detail modal |
| `vscode-layout-flag.spec.ts` | Frontend:VsCodeLayout flag |

### `mockups/` — 5 specs

| Spec | Summary |
|------|---------|
| `chat-window-next-gen-mockup.spec.ts` | @mockup chat-window-next-gen |
| `meta-cycle-mockup-screenshot.spec.ts` | meta-cycle mockup screenshots — overview, last cycle, configuration, banner states |
| `orchestrator-prep-mockup-screenshot.spec.ts` | orchestrator-prep mockup: low-autonomy and fully-auto board states |
| `task-progress-tracking-mockup.spec.ts` | task-progress-tracking mockup |
| `vscode-layout-mockup.spec.ts` | @mockup vscode-layout |

### `orchestrator/` — 11 specs

| Spec | Summary |
|------|---------|
| `orchestrator-config-panel.spec.ts` | Orchestrator logic config (side-sheet Logic tab) |
| `orchestrator-project-chat.spec.ts` | orchestrator project chat |
| `orchestrator-review-subsection.spec.ts` | (no description) |
| `orchestrator-side-sheet-position.spec.ts` | Orchestrator side sheet position |
| `orchestrator-side-sheet.spec.ts` | Orchestrator side sheet |
| `orchestrator-steering.spec.ts` | Orchestrator steering |
| `project-chat-bug-directive.spec.ts` | Project chat — Slice E /bug directive |
| `project-chat-context-awareness.spec.ts` | Project chat context awareness |
| `project-chat-fix.spec.ts` | Project chat fix - silent drop, sluggishness, parallel use |

### `perf/` — 3 specs

| Spec | Summary |
|------|---------|
| `perf-baseline.spec.ts` | Frontend perf baseline |
| `perf-frontend.spec.ts` | Frontend perceived latency |
| `perf-stress.spec.ts` | Frontend stress: render perf at scale |

### `project/` — 15 specs

| Spec | Summary |
|------|---------|
| `project-analysis-reports.spec.ts` | empty state renders manual triggers and schedule rows |
| `project-docs-section.spec.ts` | Project detail — Security & Architecture sections |
| `project-docs-sections.spec.ts` | Project docs sections |
| `project-drift-architecture-marble.spec.ts` | no architecture model: empty state with explanatory copy |
| `project-drift-overview.spec.ts` | empty state: section visible with action buttons; no scored block |
| `project-identity.spec.ts` | Project identity & running prominence |
| `project-observability-panel.spec.ts` | rail entry opens the observability panel and shows empty state when no bus traffic |
| `project-product-runtime-panel.spec.ts` | rail entry opens the product runtime panel and shows empty state when no events |
| `project-security-panel.spec.ts` | empty state - no baseline, no reviews, all actions render |
| `project-shell-rail.spec.ts` | opens the project shell from the kanban tab and lands on Overview |
| `project-steering-docs.spec.ts` | Project detail - Steering Docs section |
| `project-tab-tokens.spec.ts` | Per-project token total badge |
| `project-token-usage-panel.spec.ts` | empty state - no orchestrator entries renders explicit empty copy |
| `project-uxui-panel.spec.ts` | empty state - no design folder, all action buttons render |
| `workspace-token-timeline.spec.ts` | Workspace token timeline |

### `system/` — 8 specs

| Spec | Summary |
|------|---------|
| `caret-suppression.spec.ts` | caret suppression on non-text-input elements |
| `concept-help.spec.ts` | orchestrator concept-help on the global orchestrator card |
| `escape-modal-stack.spec.ts` | Escape modal-stack arbitration |
| `live-decision-banner.spec.ts` | live decision banner renders with reply affordance |
| `markdown-body-consolidation.spec.ts` | Markdown typography consolidation |
| `stop-no-error-modal.spec.ts` | Stop -> stopped (no error modal) |
| `update-banner.spec.ts` | update surface |
| `update-pipeline-ux.spec.ts` | Update Service pipeline UX (mocked) |

### `task-detail/` — 29 specs

| Spec | Summary |
|------|---------|
| `delete-task.spec.ts` | Delete task |
| `detail-chat-first-compression.spec.ts` | Detail page chat-first compression — Slice 1 |
| `detail-compact-and-maximize.spec.ts` | Detail view — compact command bar, pane maximize, collapsible task list |
| `detail-do-next.spec.ts` | Detail view — Do Next |
| `detail-lane-dropdown.spec.ts` | Detail view — lane dropdown |
| `detail-panes-and-git.spec.ts` | Detail view — 3-pane layout + Git view |
| `detail-view-lane-pager.spec.ts` | Detail view - lane pager |
| `detail-view-refresh.spec.ts` | Job detail view — F5 / page refresh |
| `detail-view-status-dropdown.spec.ts` | Detail view — status dropdown (discoverability) |
| `inspector-tab-default.spec.ts` | Detail inspector — default tab |
| `job-results-html-render.spec.ts` | Beautiful HTML result rendering |
| `log-overlay-centering.spec.ts` | Log overlay (maximized agent log) — centering |
| `open-failed-task.spec.ts` | open the failed screenshots-in-editors task and capture errors |
| `prompt-edit-lock.spec.ts` | Prompt editor — lock semantics |
| `prompt-save.spec.ts` | Prompt editor — Ctrl+S save & visual feedback |
| `protocol-image-flow.spec.ts` | Protocol image flow |
| `protocol-pane-cool.spec.ts` | Protocol pane — cool header + pill toggle |
| `protocol-summary-failure-banner.spec.ts` | Protocol pane — summary failure banner |
| `protocol-verdict-and-interim.spec.ts` | Protocol pane - verdict chip + interim status |
| `repository-hygiene-strip.spec.ts` | Repository hygiene - review/completed strip |
| `review-evidence-panel.spec.ts` | Review evidence panel |
| `runtime-console-capture.spec.ts` | runtime console capture |
| `session-events.spec.ts` | Detail — session events + recovery continue |
| `session-task-link-chip.spec.ts` | session->task chip transitions active -> linked and routes click |
| `task-detail-multi-commit.spec.ts` | Task-detail multi-commit chain |
| `task-detail-no-repo-level-hygiene.spec.ts` | Task detail page does not surface repo-level hygiene signals |
| `task-detail-worktree-isolation.spec.ts` | Task-detail worktree isolation |
| `triage-actions-in-detail-header.spec.ts` | Triage actions in detail header (primary + overflow) |
| `verbose-debug-overlay.spec.ts` | Verbose Debug overlay - task workbench |

### `visual-evidence/` — 7 specs

| Spec | Summary |
|------|---------|
| `job-screenshots-in-protocol.spec.ts` | Job artifacts harvesting (images-and-protocol) |
| `prompt-screenshot-attachment.spec.ts` | Prompt editor — screenshot attachments |
| `readme-screenshots.spec.ts` | readme screenshots — board, detail, protocol pane |
| `session-chip-screenshot.spec.ts` | session chip — visual capture (continued / lost / fresh) |
| `task-description-image-lightbox.spec.ts` | Task description image lightbox |
| `unified-dialog-screenshots.spec.ts` | Unified confirm + notify visuals |
| `visual-evidence-strip-and-reel.spec.ts` | Visual evidence: per-task strip + lightbox + workspace reel |

<!-- COVERAGE-MAP-END -->
