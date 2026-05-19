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
- The CLI Usage sidesheet (`features/cli/components/cli-usage-sheet.ts`) participates in flex layout: when closed its host width collapses to 0, so the main board reflows instead of being overlaid.

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

**Enforced by `npm run lint:structure`**: `scripts/check-component-folders.mjs` scans every `.ts` file under `src/app/` that declares `@Component` and fails the build if two component files share a folder. The same script also warns when a component file's basename doesn't match its containing folder, but the warning is non-blocking (the codebase has a handful of intentional `<folder>-panel.component.ts` cases).

CSS linting runs with `npm run lint:css` (Stylelint, configured in `.stylelintrc.json`). Both run as part of `npm run lint`.

## Tests

`npm test` runs Vitest via `@angular/build:unit-test` (Angular 21's first-party runner). All `src/**/*.spec.ts` are picked up; `e2e/` is Playwright-only and not part of `npm test`.

**Smoke tests** (Cycle 11c). Every standalone component has a generated `<name>.spec.ts` that mounts the component with the standard provider stack (`provideZonelessChangeDetection`, `provideHttpClient` + `provideHttpClientTesting`, `provideRouter([])`) and checks the constructor + `inject()` wiring + decorator metadata don't throw. They DO NOT exercise the full render path — `detectChanges()` is wrapped in try/catch so a missing required input or per-component service stub surfaces as a console note instead of a red test. Re-generate after adding components: `node scripts/generate-smoke-specs.mjs` (skips files that already have a `.spec.ts`).

When you need a real render-path test, hand-tune the spec: seed the required `input.required<...>()` defaults via `fixture.componentRef.setInput(name, value)` and add any per-component service stubs to `providers`. Two specs are currently `it.skip` for that reason (`git-pane`, `protocol-pane`) — they need stubs for `GitPaneService` / `ClaudeSessionPollService`.
