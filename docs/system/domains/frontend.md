# Frontend Domain Map

Version: 2026-07-24
Status: System-of-record map for frontend changes.

Use this when a change touches Angular code, visual design, task-detail,
kanban, project pages, frontend polling, model selectors, menus, or Playwright
coverage.

## Global Search

The title-bar search opens a Ctrl+K command palette. V1 covers tasks (key,
title, prompt, and status text), commit messages and SHA prefixes, and file names
or paths on each project's working branch. Task matches are ranked immediately
from the in-memory board snapshot, with an exact task key first and a warm
response target below 300 ms.

Repository-backed results use
`GET /api/search?q=<query>&domains=tasks,commits,files&limit=<count>`. The
`domains` value is a comma-separated subset of `tasks`, `commits`, and `files`;
`limit` is optional and bounded by the backend. The JSON response contains the
normalized `query`, an array for each requested domain, per-domain `errors`,
and `durationMs`. Git commit and file lookup reuse the HEAD-keyed cache rather
than maintaining a search index.

Results are grouped by domain and carry project identity. Commit results open
the diff surface, documentation files open the Wiki, and other files open the
project Git view. Queries shorter than two characters return empty result
groups, and a failed domain reports an error without hiding successful domains.

## Entry Points

- [frontend/AGENTS.md](../../../frontend/AGENTS.md) contains frontend-scoped agent
  rules and wins for files under `frontend/`.
- [Stable view URLs](../contracts/stable-view-urls.md) defines the canonical,
  agent-constructible URL grammar and compatibility policy.
- [frontend/e2e/README.md](../../../frontend/e2e/README.md) covers Playwright setup,
  fixtures, screenshots, and conventions.
- [docs/quality/frontend/design-system.md](../../quality/frontend/design-system.md) defines the visual contract.
- [docs/quality/frontend/style-guide/](../../quality/frontend/style-guide/README.md) is the UI vocabulary and component
  style source.
- [docs/quality/design-principles.md](../../quality/design-principles.md) is the UX contract.
- [docs/quality/frontend/performance.md](../../quality/frontend/performance.md) is the frontend performance
  playbook.
- [docs/quality/frontend/audits/architecture-review-2026-05-09.md](../../quality/frontend/audits/architecture-review-2026-05-09.md)
  is the maintainability map for large components and service extraction.

## Studio route restoration

The active Studio surface is described by one canonical hash path. Board and
project scope, Hub rail, Wiki page or folder, Workbench id, public task key and
active detail tabs, Epics scope, and Settings section restore from that route.
The top-level query string is no longer an application routing surface. Legacy
`task`, `job`, and `watchPath` query parameters remain read-only migration
inputs and canonicalize after resolution.

Route hydration wins over the locally persisted tab collection. State-to-route
mirroring is enabled only after public project slugs or task keys have resolved,
preventing a stale local tab from erasing a copied route during cold boot.
Surface and subview synchronization uses `replaceState`; board filters remain an
orthogonal sibling hash segment. Workspace Settings sections use the same
`#/workspace/settings[/<section>]` path convention; older loose token and
screenshot routes remain migration inputs. The full schema, transient-state
boundary, route map, and visual ownership diagram are in
[Studio Route Restoration](../../concepts/studio-route-restoration.md).

The workspace Activity Feed is the embedded `#/feed` main view and is opened
by the Activity icon. Its 500-event backend snapshot is rendered through a
bounded, variable-height history window based on the Activity scroll fix, so
live prepends preserve the operator's reading position without FixedSize
virtual-scroll assumptions. Only fresh `alert` events contribute to the icon
badge. The older project-scoped modal remains a quick-access compatibility
surface and shares the same live feed store. Its bounded panes use Deck-Panel
v1, and alert treatment follows the AGT-2410 acute-only status contract.

## Key Code

- `frontend/src/app/features/board/`: kanban lanes, task cards, project tabs,
  filters, and task creation. Post Processing cards project the live
  auto-review status snapshot into a compact current-step or elapsed-wait
  indicator; the lane header reconciles those visible cards as active versus
  waiting, with machine-lock gate queueing remaining a distinct waiting state.
- `frontend/src/app/features/board/components/epic-overview-screen/`: the
  read-only Epics overview (`#/epics`, studio tab `epics:<project|__all__>`).
  It fetches `GET /api/epics` (archive-inclusive) and splits rollups into
  `Active` and `Completed` sections (`epic-overview-section-active` /
  `epic-overview-section-completed`); a rollup counts as Completed only when
  `subTaskTotal > 0` and every child is done. Archived zero-member cleanup
  epics are hidden, while completed epics with historical children stay visible
  as history. Each card shows an `x / y done` count, a done/in-progress/open
  progress bar, and expands (`epic-overview-expand`) to child rows carrying
  lane status and a project colour dot (`epic-overview-open-sub` /
  `epic-overview-sub-project`). The screen only navigates (it emits `openTask`);
  epic assignment stays on the board (create dialog + card context menu).
  `EpicCreateDialogComponent` requires a title and goal description before it
  posts. Contract locked by `e2e/board/epic-overview-history.spec.ts` (mocked
  routes, both themes) and `e2e/board/epic-overview-screen.spec.ts` (real
  backend seed + navigate).
- `frontend/src/app/features/task-detail/`: task detail shell, protocol pane,
  prompt pane, git pane, timeline, pipeline overview, and command surfaces.
  The middle inspector uses the fixed `Task | Activity | Result` order. Task
  renders `prompt.md` and the read-only refinement projection from run/log and
  steering-history evidence. When `enrichment-report.json` exists, Task keeps
  the authored prompt independently readable and renders a quiet responsive
  report beside it with status, detected areas, every append/reject decision,
  original/appended/final token counts, selector-ledger attribution, nullable
  input-cost estimate, exact expandable blocks, and warnings/errors. The report
  stacks below the prompt on narrow panes and uses background tint, badge, and
  dot status treatment without a coloured left accent line. Activity and Result
  retain their existing live and settled-run defaults.
  Escalated tasks render a borderless, collapsible decision section that
  reconciles delivery and decision state in one sentence, places the primary
  reissue, accept-as-is, and abort decisions beside the recommendation, lists
  reissue timestamps and triggers from the timeline, and shows only recorded
  gate, review, and delivery context. When none of that structured context is
  present, the section renders one compact empty-context line instead of three
  placeholder columns.
  The Runs modal also shows the current operator-owned review-attempt epoch and
  the closed cycle history, including requeue reason, lane crossing, and rotated
  artifact count.
  Timeline and steering text is ANSI-sanitised before rendering. Timeline rows
  project each fact once across title, summary, and badges, omit permanent
  defaults and zero counts, and disclose the exact members behind source counts.
  Execution-context rows keep model and thinking level visible without repeating
  the implied CLI. Code Review keeps the last available grade visible with its
  date when it belongs to an older delivery. The task-detail Docs tab presents
  rendered result documents before prompt and raw artifacts, with per-document
  anchors and technical file metadata disclosed from the document details menu.
  Each review row also shows its council reaction, including
  per-finding rulings and the linked follow-up round. Reviews without a reaction
  sidecar expose that audit gap explicitly.
- `frontend/src/app/features/project-detail/`: project shell and project-level
  quality, settings, architecture, runtime, drift, and supervisor panels. The
  left rail (`project-shell`) is a collapsible-segment tree
  (Insight / Quality / Context / Config). Its inventory and grouping are
  defined once in `project-shell/project-shell.config.ts`; edit that file, not
  the template, to add or move a rail entry. Context contains Architecture,
  Project Graph, Wiki, Agent Docs (the AGENTS.md-style instructions agents read
  on their own, key `steering`), and Prompts. Project Graph (`project-graph`)
  consumes the read-only
  `GET /api/projects/{projectName}/graph` catalog and offers a bounded component
  graph plus a complete component list. The catalog retains unavailable managed
  projects instead of omitting them, resolves internal manifest references only,
  and reports independent repository revision / dirty state with snapshot schema,
  generator version, and capture time. It deliberately does not infer a code-call
  graph, runtime behavior, or architecture grade. The prompt-readable companion
  and regeneration command live in
  [architecture/project-map.md](../architecture/project-map.md); each regeneration
  also writes a dated JSON envelope under `architecture/project-map-history/`.
  The former Runtime Prompts placeholder rail is intentionally removed. The Wiki / Docs rail
  (`project-detail/components/project-wiki-section/`) renders the physical
  `docs/` folder tree from the project's checkout or configured
  `wikiSourceBranch`. The Wiki header shows the effective branch and commit;
  non-checkout sources are read-only. Checkout sources support real create / move / rename / delete
  operations, and shows a per-doc History panel (model / when / why + git log);
  its endpoints and tree contract are documented in
  [docs/system/contracts/wiki-tree.md](../contracts/wiki-tree.md).
- `frontend/src/app/features/project-detail/components/project-url-preview-tab/`
  owns each online and offline Project URL preview. Its shared context header
  reads repository name, current branch, HEAD, and ahead/behind distance from
  `GET /api/projects/{projectId}/urls/{urlId}/context`. The backend resolves Git
  at the preview command's effective working directory, prefers the branch
  upstream as comparison line, and falls back to the project's integration
  line. The header derives its open-task count and expandable task links from
  the existing Task Server-backed grouped task snapshot, excluding completed
  and archived cards. Readiness and last-start diagnosis remain independent, so
  a failed preview keeps both the source context and its compact failure detail.
- `frontend/src/app/features/project-detail/components/workbench-viewer/` is the
  isolated Workbench host. Explorer discovery is lazy per expanded project;
  Pulse reuses the same catalogue as a thinking inbox. Repository HTML runs only
  in an opaque-origin `srcdoc` iframe with the Workbench CSP. A source-checked
  message boundary maps docs-relative links to the in-app Wiki and opens absolute
  HTTP(S) links in a new tab without exposing host APIs or credentials. An inert
  DOM parse moves artifact nodes into a fixed policy-first wrapper. Workbench
  pages expose Maximize and `Open in Wiki` actions, and dirty working-tree
  content is labelled as uncommitted instead of receiving the current HEAD
  revision. An inert parse discovers the
  [inline decision convention](../contracts/workbench-inline-decisions.md).
  Studio-owned controls are injected inside the sandbox, while the source HTML
  remains a readable list. The host validates every reported id against that
  parse and the existing decision service atomically persists answers, comments,
  actor, timestamp, and the prefilled feature-card proposal in `workbench.json`.
- `frontend/src/app/features/project-detail/components/project-overview-dashboard/`:
  the operator-first Project Overview composition. It presents project outcomes,
  important runtime entry points, deployment readiness, and work requiring
  attention. It delegates URL status and start-in-place behavior to
  `project-overview-urls/`, and delegates publishing actions to the existing
  `project-publish-panel/` instead of introducing competing state or commands.
- `frontend/src/app/features/project-detail/components/project-deployment-panel/`:
  the first-class Deployment destination. It consumes the same
  `GET /api/projects/{projectName}/deployment/summary` contract as Overview,
  renders repository-derived targets, launches runnable targets as visible CLI
  tasks, and keeps the bounded `deploy-stable` audit trail separate from the
  guided definition editor. The editor collects a repository script and typed,
  operator-labelled parameters, validates them through the deployment compiler,
  and previews the generated operator form before any definition is saved or run.
- `frontend/src/app/features/project-detail/components/project-test-runs-panel/`:
  the Test Quality run pipeline. It shows planned, running, and completed
  commit-bound runs in product order, including scope, host, duration, result,
  and cards attached by the backend ancestry projection. Board cards render the
  same project-run projection as perfect, diff included, diff not included,
  pending, or no assigned run. The card evidence block also renders SHA-linked
  task-owned Remote Review build-tests grades and build/test gate logs supplied
  by the backend. It names their source and tested SHA instead of showing the
  project-run default when task-owned evidence exists.
- Project Settings owns the project-dedicated execution assignment. The
  execution card selects `local` or a healthy runner identity and persists it
  through the runtime-owned
  `PUT /api/projects/{projectName}/execution-runner` contract. A null runner is
  the local default; a remote identity makes the remote daemon the sole
  auto-pickup owner for that project. The guided check reports code channel,
  `develop`, toolchain, and no-op readiness from the host registry snapshot.
  Board cards deliberately show the actual live runner from the fenced run
  lease, not merely this configured target, so assignment and attribution
  cannot be confused. A fresh connected remote lease also drives the card's
  CURRENT running copy and the status bar's separate local / remote totals;
  a disconnected, expired, or recovering location remains an acute orphan
  candidate. These consumers reuse the grouped-board and execution-location
  snapshots and do not add another polling path. The historical target is the
  ordered, immutable route
  defined by [Runner provenance and host handoff](../../concepts/completion-review-and-remote-runner-stability.html#provenance):
  task Overview and run/pipeline detail show actual placement per agent run and
  executed step, preserve A → B → A returns, and label missing legacy data as
  unknown rather than inferring local execution.
- Project Settings starts with an editable **Project basics** section. It owns
  the workspace, display name, short code, project colour, repository checkout,
  CLI working directory, repository URL, and default coding CLI/model. It
  deliberately does not own runtime assignment state. These are the same basic
  groups shown during onboarding. Saving uses one
  `PUT /api/projects/{PROJ-NNN}` request, and clearing an optional value uses
  its explicit `clear*` field rather than an empty path or URL. The adjacent
  execution-assignment card remains the UI owner for the runner and uses its
  dedicated `execution-runner` endpoint. Save feedback must not imply
  local-runner hot reload: changing the display name, repository checkout, or
  working directory requires a backend restart before the already-instantiated
  local runner may pick up work.
- `frontend/src/app/services/task.service.ts`: task API integration, optimistic
  lane moves, reorder, and rollback.
- `frontend/src/app/services/cli-catalog.store.ts`: boot-hydrated CLI model
  catalog cache.
- `frontend/src/app/features/orchestrator/state/orchestrator-composer-model.service.ts`:
  workspace-persistent GPT model and reasoning selection for the canonical
  coding-agent-chat footer. It projects the complete live Codex catalogue and
  distinguishes an explicit operator choice from an inherited default.
- `frontend/src/app/components/menu/`: text-only menu component.
- `frontend/src/app/components/cli-model-selector/`: shared CLI/model picker.
- `frontend/src/app/components/task-reference-microcard/`: compact, accessible
  task reference control shared by Wiki and coding-agent-chat markdown. The
  host hydrator batches bare registry-key candidates and owns task-tab
  navigation; code blocks and unknown shortcodes remain plain text.
- `frontend/src/app/features/polling/`: bounded polling services for detail
  panes and runtime data.
- `frontend/src/app/features/shell/components/workspace-overlays/`: the global
  Workspace Settings home. Its rail is the single navigation surface for CLI
  Management, system prompts, token usage, visual evidence, and the workspace
  summary. It does not own project onboarding or a project-source catalogue.
  Legacy CLI-admin and usage links resolve to the CLI Management section at
  `#/workspace/settings/caps`.
- The System prompts destination is the prompt registry and observability
  surface. Its overview groups runtime-step, orchestrator, drift, and framing
  templates, explains application and project pipeline override precedence, and
  provides a sortable activity table. `RuntimePromptService.Render` appends one
  row per use to `<TaskRepository>/logs/prompt-calls.jsonl` with the effective
  content hash, estimated rendered-input tokens, timestamp, and any available
  project, step, and model context. The API aggregates total and seven-day
  calls, last call, a 14-day series, current and historical versions, and
  historical theoretical input cost through `TokenPricing`. Unknown models
  remain explicitly unpriced. Review actions check the static usage catalogue,
  repository references, and project pipeline overrides, then persist
  `prompts/runtime/<name>.md.meta.json`.
  The durable source, precedence, companion, telemetry, and cost rules are
  defined by the
  [runtime prompt registry contract](../contracts/runtime-prompts.md).
  Result-quality benchmarking by prompt version is deliberately vNext. The
  versioned call ledger is its data foundation, but this surface does not claim
  that call volume or cost measures outcome quality.
- `frontend/src/app/features/shell/components/onboard-project-dialog/`: the
  project onboarding workflow. Its roomy, scrollable form groups project
  identity, repository paths/URL, and execution defaults without a source-type
  selector. It calls `POST /api/projects`, then refreshes the registry-backed
  workspace tree so the new project appears immediately. Required-field,
  short-code, absolute-path, and HTTP(S)-URL errors stay visible without
  discarding the values already entered.

## Project Overview Contract

The default `#/projects/<project-id>` rail is an operator dashboard. It answers what
was delivered, what changed, what is reachable, and what deserves attention.
Machine configuration does not belong in this view. Watch path, working
directory, repository path, CLI readiness and status, clean-context settings,
and project sessions remain in Project Settings.
Project regression signals remain available from the Test Quality rail rather
than competing with the Overview's operator summary.

The dashboard is a projection over existing domain truths:

| Dashboard block | Read model or capability | Detail owner |
|---|---|---|
| Delivered work | `GET /api/projects/{projectName}/throughput`, including archived task history and exact rolling 24-hour and 7-day windows | Board and task history |
| Token use | `GET /api/projects/{projectName}/token-usage/summary`, including rolling 24-hour and 7-day totals | Token Usage rail |
| Project URLs | Embedded project URLs from `GET /api/workspaces`; command-working-directory Git identity from `GET .../context`; Task Server-backed open-card links; host-side readiness probes; per-embed URL/start settings; and owned process start, snapshot, output, and stop through `POST .../start` plus `GET/DELETE .../process` | Project URL embed, Project URLs rail, registry, and grouped task snapshot |
| Deployment readiness | `GET /api/projects/{projectName}/deployment/summary`, the shared DEP-1 read model for the last stable deployment and current pending commit delta | Deployment domain |
| Wiki activity | Initial `GET /api/projects/{projectName}/wiki/pulse?feedLimit=6`, then a visible-only conditional poll of `GET /api/projects/{projectName}/wiki/recent?limit=6` | Wiki rail |
| Planning work | Active planning-mode tasks from the current board snapshot | Task detail and Board |
| Visual evidence | `GET /api/projects/{projectName}/visual-evidence`, with append-only review receipts shared with task detail | Existing Visual Evidence detail surface |
| Publishing | Publish targets from `GET /api/projects/{projectName}/snapshot`, rendered by the existing publish panel | Publishing panel |

The Overview limits URL, Wiki, planning-task, and commit lists to compact
previews and links to the owning detail surface. Each data request fails
independently so one unavailable source does not blank the dashboard. Numeric
metrics use tabular figures.

Overview and Deployment both use the DEP-1 summary contract; neither parses
deployment history in the frontend. Runnable targets default to the latest
successful test run and carry its id, exact commit, and Head distance into the
durable visible deployment task. Selecting Head is an explicit exception that
requires an operator reason. Publishing controls remain owned by the existing
publishing surface.

The Overview owns a compact Visual Evidence review queue over delivered task
screenshots. Acknowledgements reuse the append-only review-evidence log, so the
Overview and task detail share one durable unseen/reviewed truth. The queue
preserves task and artifact provenance, keeps reviewed receipts from becoming
unseen again, and renders missing reviewed artifacts as no longer actionable.

## Invariants

- Angular components are standalone. Do not introduce NgModules.
- State should use Angular signals and existing stores before new state
  mechanisms.
- Durable user-owned frontend mutations are optimistic by default: snapshot,
  local signal update, fire request, rollback plus toast on error.
- Destructive operations and runner side effects stay spinner-backed rather than
  optimistic.
- Menus are text-only. Do not add leading icons to menu rows.
- Before adding visual variants, check the style guide and update it if a new
  pattern is truly needed.
- Use stable `data-testid` hooks for Playwright selectors.
- The Epics overview keeps completed and archived epics visible as history
  instead of dropping them once finished; history renders quietly with no acute
  status signals (R4), and each epic's progress counts are the sum of that
  epic's visible children (R3). The only rollups withheld from the overview are
  empty archived cleanup epics (zero members). See the epic domain contract in
  [docs/system/domains/tasks.md](./tasks.md#epic-lifecycle).
- Workspace-level CLI administration is not a separate sheet. Model and
  environment management, completion contracts, sessions, usage caps, and
  token spend belong to Workspace Settings under CLI Management.
- The Orchestrator uses the standard `<cac-chat>` footer model control. In
  GPT-only mode it renders every available model and reasoning level from the
  live Codex catalogue, preserves the full selection across navigation context
  changes, and sends the effective selection with every message. Its CLI row
  still lists the complete Studio CLI vocabulary; non-Codex entries are
  disabled with the GPT-only host-policy reason so they are not mistaken for a
  quota or installation detection failure. Do not replace it with a
  routing-only badge, reduced host list, or Orchestrator-only picker.
- Project Overview remains operator-first. Do not add watch paths, repository
  paths, working directories, CLI health, clean-context controls, or session
  administration back to the Overview; those facts belong to Project Settings.
- Backlog Triage is not a project navigation surface. Do not add a project
  Backlog tab, Explorer entry, activity-bar entry, or project-scoped board
  filter coupling. Persisted legacy `backlog` studio tabs are discarded during
  tab-state restoration. This does not affect the Board's `0-backlog` lane,
  which remains the lifecycle landing lane for new tasks.

## Verification

- Visual or behavioral changes require relevant Playwright specs. Add or extend
  a spec when none covers the changed behavior.
- Capture screenshots for review-relevant states and persist them in the task
  `results/` folder when they must survive test cleanup.
- UI performance regressions are measured in the browser using the helpers in
  `frontend/e2e/helpers/timing.ts`.
- Pure frontend refactors still need component or unit tests when they move
  state, inputs, outputs, or service contracts.
- The focused component contract for Project Overview is
  `frontend/src/app/features/project-detail/components/project-overview-dashboard/project-overview-dashboard.spec.ts`.
  Compact URL status, start gating, and project-switch safety are covered by
  `frontend/src/app/features/project-detail/components/project-overview-urls/project-overview-urls.spec.ts`.
  Its production dashboard navigation, URL start reuse, partial read models,
  both themes, overflow guard, and review screenshots are covered by
  `frontend/e2e/project/project-overview-dashboard.spec.ts`. The interactive
  design contract is covered separately by
  `frontend/e2e/mockups/project-overview-dashboard-mockup.spec.ts`.
