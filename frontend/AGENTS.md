# Frontend Instructions

## UI Verification — Playwright is mandatory after visual changes

After **every** frontend change under `frontend/src/` that touches layout, spacing, styling, component templates, or interaction states, you must verify with Playwright before declaring the task done. Static type-checks and unit tests do not catch UI regressions; the E2E suite does.

Workflow:

1. Make sure backend (`http://localhost:5030`) and frontend (`http://localhost:4010`) are running. Tests do **not** spawn them — they fail fast if missing.
2. Run the relevant spec, or the full suite for cross-cutting changes:
   ```powershell
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
- The CLI Usage sidesheet (`components/cli-usage-sheet.ts`) participates in flex layout: when closed its host width collapses to 0, so the main board reflows instead of being overlaid.
