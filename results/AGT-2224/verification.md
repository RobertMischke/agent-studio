# AGT-2224 verification

## Review gaps addressed

- The existing deferred integration merge now invokes `PreMainTestGate` when
  its configured target resolves to `main`.
- The gate runs once at the mandatory `full` level on the exact source SHA.
  Both source and target refs are checked again after the suite, and `main`
  advances only by fast-forward to the immutable tested SHA, never the movable
  source branch name.
- A red or incomplete full-suite result leaves `main` unchanged and records a
  visible failed merge step plus `post-steps/pre-main-test-gate-*.log`.
- A passed work-package subset exposes `test-level=work-package` and
  `full-suite=not-run` from the green status icon. The screenshots below prove
  the same state in dark and light themes.

## Automated verification

- Rebased conflict-free onto `origin/develop` at `61638164` on 2026-07-24,
  then repeated the focused verification below from the updated task branch.
- Focused staged execution and release-boundary tests:
  `27 passed, 0 failed`, including a source-ref move during the suite that
  proves an untested SHA cannot reach `main`.
- Orchestrator, project settings, build gate, and task-transition regression
  tests: `98 passed, 0 failed`.
- Overview component tests: `56 passed, 0 failed`.
- Focused Playwright spec
  `e2e/task-detail/pipeline-step-explanations.spec.ts`: `1 passed, 0 failed`.
  Its harvested `playwright/index.json` names the executed spec, test, and both
  attached theme captures instead of an `unknown` entry with no files.
  The fully mocked spec ran against this task worktree's frontend on an
  isolated temporary port. Stable preflight was unavailable because its
  unrelated Vite session could not resolve `wiki-source-badge.component`;
  no artifact from that failed preflight is retained here.
- ESLint on the changed overview and Playwright files: passed.
- Frontend component-folder structure check: passed.
- Full frontend lint remains blocked by two pre-existing
  `@typescript-eslint/no-empty-function` findings in
  `studio-shell.component.spec.ts` lines 710 and 711. That unrelated file was
  not changed.
- The standalone component-size check also reports existing baseline drift on
  current `origin/develop`. For the task-owned Overview component, Develop has
  1947 TypeScript lines and this branch has 1943, so AGT-2224 reduces rather
  than introduces that violation.

The measurable subset regression uses an 80 ms selected command and a 650 ms
omitted command, and requires the work-package run to save at least 350 ms
versus the full suite.

## Visual evidence

- `pipeline-subset-coverage-tooltip--dark.png`
- `pipeline-subset-coverage-tooltip--light.png`
