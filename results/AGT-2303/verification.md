# AGT-2303 verification

Date: 2026-07-24

## Delivered behavior

- Remote runner load is woven into the existing running pulse through a quiet
  token-based tone and a static mismatch ring. No separate widget was added.
- The tooltip reports the one-minute load, CPU core capacity, normalized load,
  and active remote slots.
- High load with zero reported runs and several reported runs with almost no
  load produce a quiet consistency hint.
- Fresh host telemetry is refreshed every 30 seconds without replacing the
  visible host registry. Samples older than five minutes are not presented as
  current.
- Motion collapses to zero duration when reduced motion is requested.

## Automated verification

- `npm --prefix frontend run test -- --include src/app/features/shell/components/status-bar/status-bar-host-load.spec.ts --include src/app/features/remote-hosts/services/remote-hosts.service.spec.ts --include src/app/features/shell/components/status-bar/status-bar.spec.ts`
  - Result: 3 files passed, 12 tests passed.
- `npm --prefix frontend run build`
  - Result: passed.
  - Existing warning: the initial bundle exceeds the configured 3 MB budget.
- Focused ESLint and Stylelint over all changed TypeScript and SCSS files
  - Result: passed.
- `PW_BASE_URL=http://127.0.0.1:4020 JOB_RESULTS_DIR=.../results/AGT-2303 npm --prefix frontend run e2e -- e2e/layout/status-bar-host-load.spec.ts`
  - Result: 3 tests passed.
- `git diff --check`
  - Result: passed.

The full frontend lint command remains blocked by two pre-existing
`@typescript-eslint/no-empty-function` findings in
`frontend/src/app/features/studio-shell/studio-shell.component.spec.ts` at
lines 710 and 711. That file is unchanged by AGT-2303.

## Visual evidence

- `status-bar-host-load-mismatch-light--mocked.png`
- `status-bar-host-load-mismatch-dark--mocked.png`
- `playwright/index.json`
