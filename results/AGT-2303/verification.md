# AGT-2303 verification

Date: 2026-07-25

## Delivered behavior

- Remote runner load is woven into the existing running pulse through a quiet
  token-based tone and a static mismatch ring. No separate widget was added.
- The tooltip reports the one-minute load, CPU core capacity, normalized load,
  and active remote slots.
- High load with zero reported runs and several reported runs with almost no
  load produce a quiet consistency hint.
- Fresh host telemetry is refreshed every 30 seconds while the tab is visible.
  The status bar requests only the compact one-hour window and preserves a
  14-day series already loaded by the Remote Hosts page. Samples older than
  five minutes are not presented as current.
- Motion collapses to zero duration when reduced motion is requested.

## Automated verification

- `npm --prefix frontend run test -- --include src/app/features/shell/components/status-bar/status-bar-host-load.spec.ts --include src/app/features/remote-hosts/services/remote-hosts.service.spec.ts --include src/app/features/shell/components/status-bar/status-bar.spec.ts`
  - Result: 3 files passed, 14 tests passed.
- `NG_BUILD_MAX_WORKERS=2 npm --prefix frontend run build`
  - Result: passed.
  - Existing warning: the initial bundle exceeds the configured 3 MB budget.
- Focused ESLint and Stylelint over all changed TypeScript and SCSS files
  - Result: passed.
- `npm --prefix frontend run lint`
  - Result: blocked by three pre-existing ESLint findings in unchanged files:
    two `@typescript-eslint/no-empty-function` findings in
    `studio-shell.component.spec.ts` (lines 710-711) and one
    `@typescript-eslint/no-unused-vars` finding in
    `code-review-panel.component.ts` (line 26).
- `PW_BASE_URL=http://127.0.0.1:4020 JOB_RESULTS_DIR=.../results/AGT-2303 npm --prefix frontend run e2e -- e2e/layout/status-bar-host-load.spec.ts`
  - Result: 3 tests passed.
- `git diff --check`
  - Result: passed.

The branch was rebased from the stale July 24 base onto the current
`origin/develop`. The focused test fixture was updated for the current auth
gate and background shell requests before the screenshots were recaptured.

## Visual evidence

- `status-bar-host-load-mismatch-light--mocked.png`
- `status-bar-host-load-mismatch-dark--mocked.png`
- `playwright/index.json`
