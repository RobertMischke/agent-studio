# Frontend Domain Map

Version: 2026-06-09
Status: System-of-record map for frontend changes.

Use this when a change touches Angular code, visual design, task-detail,
kanban, project pages, frontend polling, model selectors, menus, or Playwright
coverage.

## Entry Points

- [frontend/AGENTS.md](../../frontend/AGENTS.md) contains frontend-scoped agent
  rules and wins for files under `frontend/`.
- [frontend/e2e/README.md](../../frontend/e2e/README.md) covers Playwright setup,
  fixtures, screenshots, and conventions.
- [docs/frontend/design-system.md](../frontend/design-system.md) defines the visual contract.
- [docs/frontend/style-guide/](../frontend/style-guide/README.md) is the UI vocabulary and component
  style source.
- [docs/product/design-principles.md](../product/design-principles.md) is the UX contract.
- [docs/frontend/performance.md](../frontend/performance.md) is the frontend performance
  playbook.
- [docs/frontend/audits/architecture-review-2026-05-09.md](../frontend/audits/architecture-review-2026-05-09.md)
  is the maintainability map for large components and service extraction.

## Key Code

- `frontend/src/app/features/board/`: kanban lanes, task cards, project tabs,
  filters, and task creation.
- `frontend/src/app/features/task-detail/`: task detail shell, protocol pane,
  prompt pane, git pane, timeline, pipeline overview, and command surfaces.
- `frontend/src/app/features/project-detail/`: project shell and project-level
  quality, settings, architecture, runtime, drift, and supervisor panels. The
  left rail (`project-shell`) is a collapsible-segment tree
  (Insight / Quality / Context / Config). Its inventory and grouping are
  defined once in `project-shell/project-shell.config.ts`; edit that file, not
  the template, to add or move a rail entry. Context contains Architecture,
  Wiki, Agent Docs (the AGENTS.md-style instructions agents read on their own,
  key `steering`), and Prompts. The former Runtime Prompts placeholder rail is
  intentionally removed. The Wiki / Docs rail
  (`project-detail/components/project-wiki-section/`) renders the physical
  `docs/` folder tree directly, supports real create / move / rename / delete
  operations, and shows a per-doc History panel (model / when / why + git log);
  its endpoints and tree contract are documented in
  [docs/contracts/wiki-tree.md](../contracts/wiki-tree.md).
- `frontend/src/app/services/task.service.ts`: task API integration, optimistic
  lane moves, reorder, and rollback.
- `frontend/src/app/services/cli-catalog.store.ts`: boot-hydrated CLI model
  catalog cache.
- `frontend/src/app/components/menu/`: text-only menu component.
- `frontend/src/app/components/cli-model-selector/`: shared CLI/model picker.
- `frontend/src/app/features/polling/`: bounded polling services for detail
  panes and runtime data.

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

## Verification

- Visual or behavioral changes require relevant Playwright specs. Add or extend
  a spec when none covers the changed behavior.
- Capture screenshots for review-relevant states and persist them in the task
  `results/` folder when they must survive test cleanup.
- UI performance regressions are measured in the browser using the helpers in
  `frontend/e2e/helpers/timing.ts`.
- Pure frontend refactors still need component or unit tests when they move
  state, inputs, outputs, or service contracts.
