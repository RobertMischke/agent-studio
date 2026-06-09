# Frontend Instructions

## UI Verification — Playwright is mandatory after visual changes

After **every** frontend change under `frontend/src/` that touches layout, spacing, styling, component templates, or interaction states, you must verify with Playwright before declaring the task done. Static type-checks and unit tests do not catch UI regressions; the E2E suite does.

Workflow:

1. Make sure backend (`http://localhost:5030`) and frontend (`http://localhost:4010`) are running. Tests do **not** spawn them — they fail fast if missing.
2. Run the relevant spec, or the full suite for cross-cutting changes (sh — never PowerShell):
   ```sh
   npm --prefix frontend run e2e                          # full suite, headless
   npm --prefix frontend run e2e -- e2e/cli-usage.spec.ts # single spec
   npm --prefix frontend run e2e:ui                       # interactive UI mode
   npm --prefix frontend run e2e:headed                   # watch the browser
   SKIP_BILLABLE=1 npm --prefix frontend run e2e          # skip CLI-quota-burning specs
   ```
3. If your change isn't covered by an existing spec, **add or extend one** as part of the same change. Regression coverage is the deliverable, not optional.
4. For CLI-execution changes (Claude / Codex / Copilot start path, model wiring, quota), run `claude-hello-world.spec.ts` end-to-end. It uses real quota (~10s, one Haiku call) but is cheap.

Browser-based smoke check (separate from E2E) when you need a visual eyeball:

- The dev server is at `http://localhost:4010`. Verify the changed state renders correctly, text is visible, spacing/alignment look intentional, and detail panels/dialogs involved in the change open and behave.

The full E2E setup, conventions, helpers, and authoring rules live in [e2e/README.md](e2e/README.md).

## Selector convention for E2E tests

When you add interactive elements that a test will need to click or assert on, prefer in this order:

1. **`data-testid="..."`** on the element. Stable, intentional, decoupled from styling.
2. ARIA role + accessible name (`getByRole('button', { name: 'Add Task' })`).
3. Visible text — only for stable user-facing copy.

Never select by CSS class names; they belong to styling and change often.

## Architectural notes

- Standalone components only. No NgModules.
- State via Angular signals; service singletons go in `app/services/`.
- Keep the dark Catppuccin-inspired direction.
- The detail view is a simple protocol view — don't add tabs or metrics grids unless the product direction changes.
- **Menus are text-only.** `<app-menu>` rows never carry decorative icons. The `MenuRow` type has no `icon` field and the template renders no icon span. The single allowed leading affordance is `leadingGlyph` (the coloured-initial chip used by the project picker). Icons elsewhere (toolbars, chips, lane glyphs, the chat model badge pill) are fine. Full rationale in the root AGENTS.md under "Menu surfaces are text-only".

## Run-Switcher UI contract

The task detail view treats every re-open, transition back to Ready, and auto-review reissue as a new run. The Timeline tab renders chronological run cards with the transition between adjacent runs. The Overview pipeline block renders the current run first, then older archived attempts through the Run-Switcher (`Run #1`, `Run #2`, etc.).

When changing run history or pipeline display code, keep the Run-Switcher backed by archived pipeline execution records, not by the current in-memory record alone. Tests must cover that older runs stay selectable and that the current run remains the active/default pipeline view.

## Spacing: tokens, never raw px

New SCSS reads `padding`, `margin`, and `gap` from the design-token scale in [`src/styles/_tokens-semantic.scss`](src/styles/_tokens-semantic.scss); raw px in those properties is forbidden. The base scale lives at the top of that file as `--studio-spacing-1` … `--studio-spacing-7` (4 / 8 / 12 / 16 / 24 / 32 / 48 px). Semantic aliases sit alongside it: `--studio-rail-*` (collapsed lane-rail), `--studio-modal-padding-*`, `--studio-row-*` (density rows). Reach for an existing alias before introducing a new one.

Why: the lane-rail bug (operator 2026-05-28) was a symptom of per-element intrinsic sizing. Without a token vocabulary the rhythm drifts every time someone tweaks a single child; with tokens every consumer shares the same scale and `_tokens-semantic.scss` is the only place the px number is written.

Stylelint enforces this scoped per file via `scale-unlimited/declaration-strict-value` on `padding*` / `margin*` / `gap` / `row-gap` / `column-gap` in [`.stylelintrc.json`](.stylelintrc.json). Today the rule is ERROR for `src/app/features/board/components/task-column/task-column.scss` (the lane-rail block); the rest of the codebase is grandfathered while the cleanup task lifts more files into the enforced list. When you write a new SCSS file, opt it in by adding it to that override; when you touch a legacy file, prefer the migration over adding more raw px.

If a value legitimately doesn't fit the scale (true one-off like a 1 px hairline, an animation frame, a sub-pixel border), inline a `/* stylelint-disable-next-line scale-unlimited/declaration-strict-value */` with a short rationale on the same line.

## Side-sheet layout contract (`<app-orchestrator-side-sheet>`, `<app-kanban-filter-sidesheet>`)

Both right-edge side sheets are **panels that push the workspace, not overlays that float over it**. The push behaviour rides on three coordinated pieces; break any one and the panel either floats, overlays, leaves a transparent gap, or stacks on the wrong side. Locked by the `open pushes studio-shell + inner panel fills host` test in `e2e/orchestrator-side-sheet-position.spec.ts`.

> History: a third sheet, `<app-cli-usage-sheet>`, used to live here. Its quota glance and per-CLI session inventory were folded into the global Workspace Settings home (the CLI Management / "Usage caps" section, `features/cli/components/cli-admin-panel`) so CLI usage has a single hub; the loose sidesheet was retired. The status-bar "Usage" button now opens that home section instead of a parallel sidesheet.

1. **Flex parent.** In `app.html` the `vsCodeLayout` branch wraps `<app-studio-shell>` + the side sheets in `<div class="app-shell">`, styled `display: flex; flex-direction: row-reverse`. Row-reverse keeps the studio shell on the left (consuming the remaining space via `flex: 1 1 auto`) while the side sheets dock on the right edge in natural DOM order. Without the flex parent the sheets fall back to block layout and stack vertically.

2. **Caller `:host` width animation.** Each side sheet's own SCSS owns the open/close animation:
   ```scss
   :host { display: block; width: 0; transition: width 0.22s ease;
           overflow: hidden; flex: 0 0 auto; }
   :host(.is-open) { width: min(<callerWidth>px, 9Xvw); }
   ```
   `overflow: hidden` is load-bearing — without it the inner panel can render past the host's collapsed 0px width and float over the workspace. `flex: 0 0 auto` keeps the host honouring its specified width (no flex-grow surprises). Callers pick their own px width (kanban-filter 320, orchestrator 640); the host has no business setting a fixed width on the inner panel.

3. **Inner `<app-sidesheet>` width: 100 %.** `components/sidesheet/sidesheet.component.scss` ships `.sidesheet { width: 100% }` so the inner chrome (background, border, header / body / footer) tracks the host's animating width. A previous hard-coded `width: 360px` default left a transparent gap inside any host wider than 360px (visible as unreadable empty space in the orchestrator's 640px slot). Callers that need an explicit px width can still drive it via the `[width]` input.

Anti-patterns to reject in review:
- `position: fixed` on either sheet in `src/styles.scss` or component SCSS. Out-of-flow positioning makes push impossible. (An old workaround did this pre-`app-shell`; the new contract is now described in `styles.scss` where the workaround used to live.)
- A fixed `width: ...` on the inner `.sidesheet` BEM root (the inner panel must mirror the host).
- Adding `flex-direction: row` (non-reverse) to `.app-shell` without also reordering the HTML to put `<app-studio-shell>` first. Current order is `kanban-filter, orchestrator, studio-shell` and relies on row-reverse to dock the sheets right.

When you add a new right-edge side sheet, follow the same three-piece contract and add it to the regression spec.

## Detail-view lane control: dropdown navigates, context menu moves

The task-detail header carries a lane `<select>` (studio shell: `data-testid="studio-lane-select"`; legacy kanban header: the projected `<app-detail-header>` lane select). It is **navigation-only**: picking a lane re-points the Prev/Next pager at that lane and pages to a task already living there — it never changes the current task's lane. The single source of this behaviour is `TaskSelectionService.navigateToLane` (`features/task-detail/state/task-selection.service.ts`); the studio shell wires it through `App.onStudioLaneChange`.

**Moving a task to another lane** lives in the `⋯` overflow context menu (`data-testid="triage-overflow-btn"` → `triage-overflow-item-*`), built by `overflowActionsFor` in `features/task-detail/state/triage-actions.model.ts`. Per the menu-text-only rule above, these rows carry no icons.

**Orchestrator-controlled lanes are never manual targets.** `3-progress` (In Progress) and `4-auto-review` (Auto Review) are owned by the runner / `ReviewDecisionOrchestrator` — a task lands there because it was picked up or is being judged, never because an operator parked it. They are therefore excluded from **both** surfaces:
- the navigation dropdown options (`App.studioLaneOptions`, and the legacy `DetailHeaderComponent.laneOptions`) omit them;
- the context-menu move targets are stripped by `overflowActionsFor`, which filters any move action whose target is in `ORCHESTRATOR_CONTROLLED_LANES` (the canonical set in `triage-actions.model.ts`).

When you add a new manual-target lane or a new move affordance, route it through `ORCHESTRATOR_CONTROLLED_LANES` / `studioLaneOptions` rather than re-deriving the exclusion list inline, so the two surfaces stay in lockstep.

**Pager position is stable across a lane move.** The Prev/Next pager iterates a snapshot captured on detail entry (`LanePagerService`), not the live lane. Moving the current task out of its lane shrinks that snapshot by one and auto-advances to the next captured slug in the *original* lane — the `X / N` context does not jump to the destination lane. See `advanceAfterMutation` / `removeAndAdvance`.

Regression coverage (run with `PW_TARGET=stable`):
- `e2e/task-detail/detail-lane-dropdown.spec.ts` — dropdown pages without moving; orchestrator lanes absent from the nav options.
- `e2e/task-detail/detail-view-lane-pager.spec.ts` — context-menu moves keep the pager anchored on the original lane with the count shrinking by one.

## Feature folders + barrel imports (ADR-0034, Cycle 9h)

The frontend is organised by **feature** under `app/features/<name>/` with a uniform shape: `models/`, `state/`, `components/`, `services/`, plus a top-level `index.ts` barrel. Cross-cutting capabilities (e.g. `polling/`) follow the same shape.

**Hard rule for cross-feature imports**: import from the **barrel**, not from internal paths.

```ts
// ✅ correct — uses the barrel
import { BoardFiltersService, JobColumnComponent } from './features/board';
import { ProjectOverlaysComponent } from './features/project-detail';

// ❌ wrong — pierces the feature boundary
import { BoardFiltersService } from './features/board/state/board-filters.service';
import { JobColumnComponent } from './features/board/components/job-column';
```

Why: the barrel is the feature's **public API**. Anything not exported from it is private and can be moved/renamed/refactored without external breakage. Deep imports turn every internal file into a contract.

**Inside the same feature**, use relative imports (`./components/...`, `../services/...`) as usual — barrels are about the cross-feature boundary.

When you add a new component or service that needs to be reachable from outside its feature, add it to that feature's `index.ts`. If you find yourself wanting a deep import from outside, that's a signal: either (a) the symbol belongs in the barrel, or (b) what you're trying to do should live inside the target feature, not at the call site.

Existing deep imports across the codebase still work (they're just paths). Convert them to barrel imports as you touch the surrounding code; don't churn for its own sake.

**Enforced by lint** (Cycle 10j): `eslint.config.js` carries a `no-restricted-imports` rule that blocks `**/features/*/{components,services,models,state}/**` from outside the feature. `npm run lint` runs the full ruleset. Files under the same feature and `**/*.spec.ts` / `**/e2e/**` are exempt.

## Folder-per-component layout

Every Angular component lives in its own folder under `components/` (or at the feature root for the feature's entry component). The folder holds the component's `.ts`, `.html`, `.scss`, and `.spec.ts` together — never spread them across the parent directory.

```
components/
  cli-console/
    cli-console.ts
    cli-console.html
    cli-console.scss
    cli-console.spec.ts
```

Shared utility files (e.g. `*.util.ts`, `*.parser.ts`, `*-types.ts`, fixtures) that several components in the area depend on may stay at the parent `components/` level. The rule is one **component** per folder, not one file per folder.

**Enforced by `npm run lint:structure`**: `scripts/check-component-folders.mjs` scans every `.ts` file under `src/app/` that declares `@Component` and fails the build if two component files share a folder or if a new component file's basename doesn't match its containing folder. Existing `<folder>-panel.component.ts` descriptor cases are explicit baseline exceptions and should be removed when those components are renamed.

## Component size budgets

Component size is a lint gate, not a review preference. `npm run lint:components` runs `scripts/check-component-size.mjs` and counts each Angular component across its controller (`.ts`), template (`.html`), stylesheet (`.scss`), and total lines. New or already-small components must stay under the global limits in `scripts/component-size-baseline.json`; existing oversized components are baseline debt and may not grow. Split templates, controllers, or styles before raising a baseline.

The same guard requires external `templateUrl` files and rejects inline `template`, `style`, and `styles` metadata. This is intentionally redundant with ESLint so generated or LLM-authored components fail even before template-specific lint has to reason about the markup.

CSS linting runs with `npm run lint:css` (Stylelint, configured in `.stylelintrc.json`). CSS, component-size, and structure checks all run as part of `npm run lint`.

## Chat surfaces (`<app-chat>` is canonical)

**`<app-chat>`** (`components/chat/chat/`) is **the** chat component. The metaphor: any coding-agent / model interaction is a chat. A single generic, agent-and-model-agnostic surface should render all of it — messages, inline event cards, composer with pluggable toolbar — so the orchestrator side sheet, the per-task chat, and any future "talk to model X" surface all look and behave the same. The component is intentionally free of orchestrator-specific or task-board-specific imports so it can be **extracted as a standalone "talk to models" library** down the line.

Inputs/outputs (informally):
- `messages: ChatMessage[]` — turns, with optional `attachments`, `pending`, `error`.
- `events: ChatEvent[]` — inline state cards interleaved by timestamp (`tool-call`, `watchdog`, `rate-limit`, `decision`, `update`, `task`, `session-recovered`, `memory-refreshed`). New kinds are added to the `ChatEventKind` union, not to per-host components.
- `toolbarStart` / `toolbarEnd` (`ChatToolbarItem[]`) + `routingLabel` — composer toolbar plugin slots; the host emits whatever affordances it needs. Clicks come back via `toolbarAction({id})`.
- `compactPhaseSummary` (default `true`) — collapse the phase-summary list above the chat into a single "▸ N earlier phases" strip until the user reveals it.

Supporting bits in the same area:
- `<app-chat-row>` (`components/chat-row/`) — single-row presentation primitive. Currently used by `<app-project-chat-list>` only; `<app-chat>` keeps its richer per-message rendering (collapse-on-overflow, pending pulse, error footer) until the row supports those variants. See migration plan below.
- `<app-project-chat-list>` (`features/project-chat/components/project-chat-list/`) — **legacy** virtualised, read-only view over the per-month markdown corpus. Co-mounted opt-in (`?virtualChat=1`) in the orchestrator side sheet. The direction is to fold virtualisation into `<app-chat>` and drop this component.

Migration plan (multi-session, in order):

1. Extend `<app-chat-row>` with `<app-chat>`'s richer per-message variants (collapse-with-show-more, pending pulse, error footer, attachments).
2. Migrate `<app-chat>`'s per-message rendering to `<app-chat-row>`. Keep the composer and the events/messages merge logic in `<app-chat>`.
3. Add an optional virtualisation mode to `<app-chat>` (windowed render + spacer rows) so it can stand in for `<app-project-chat-list>` when chat history is large.
4. Delete `<app-project-chat-list>` once `<app-chat>` covers the virtualised + read-only path; flip the `?virtualChat=1` callsite to a no-op.
5. Move `<app-chat>` (and its types, `<app-chat-row>`, role-badge / phase-summary helpers) into a self-contained area with no app-specific imports so it can be lifted out as a package.

New ChatEvent kinds belong in `chat-types.ts` and get icon + label entries in `chat.component.ts` (`eventIcon` / `eventLabel`). Severity-style tints (warn / error / `session-recovered` / `memory-refreshed`) go in `chat.component.scss` as `.chat__event--<kind>` modifiers.

## Tests

`npm test` runs Vitest via `@angular/build:unit-test` (Angular 21's first-party runner). All `src/**/*.spec.ts` are picked up; `e2e/` is Playwright-only and not part of `npm test`.

**Smoke tests** (Cycle 11c). Every standalone component has a generated `<name>.spec.ts` that mounts the component with the standard provider stack (`provideZonelessChangeDetection`, `provideHttpClient` + `provideHttpClientTesting`, `provideRouter([])`) and checks the constructor + `inject()` wiring + decorator metadata don't throw. They DO NOT exercise the full render path — `detectChanges()` is wrapped in try/catch so a missing required input or per-component service stub surfaces as a console note instead of a red test. Re-generate after adding components: `node scripts/generate-smoke-specs.mjs` (skips files that already have a `.spec.ts`).

When you need a real render-path test, hand-tune the spec: seed the required `input.required<...>()` defaults via `fixture.componentRef.setInput(name, value)` and add any per-component service stubs to `providers`. Two specs are currently `it.skip` for that reason (`git-pane`, `protocol-pane`) — they need stubs for `GitPaneService` / `ClaudeSessionPollService`.
