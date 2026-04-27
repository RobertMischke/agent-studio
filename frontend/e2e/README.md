# Frontend E2E Tests (Playwright)

End-to-end regression suite for the Agent-Taskboard frontend.

## Why this exists

After every change with visual or behavioural impact in `frontend/`, run the
relevant Playwright spec(s) before declaring the task done. Static type checks
and unit tests do not catch UI regressions; this suite does.

## Prerequisites

The tests assume the dev stack is already running:

| Service  | Command                                    | URL                     |
|----------|--------------------------------------------|-------------------------|
| Backend  | `.\api.ps1 start` (from repo root)         | http://localhost:5030   |
| Frontend | `npm start --prefix frontend` (or VS Code task `Frontend: Start`) | http://localhost:4010 |

`playwright.config.ts` does not spawn these — both fail fast if missing.

## Running

```powershell
# from repo root
npm --prefix frontend run e2e            # headless run, all specs
npm --prefix frontend run e2e:ui         # interactive UI mode (debugging)
npm --prefix frontend run e2e:headed     # watch the browser
npm --prefix frontend run e2e -- e2e/cli-usage.spec.ts   # single spec
npm --prefix frontend run e2e:report     # open last HTML report
```

First-time browser install (already done in dev setup, but if the
`chromium-headless-shell` binary is missing):

```powershell
npx --prefix frontend playwright install chromium
```

## Suite layout

| File                          | What it covers |
|-------------------------------|----------------|
| `cli-usage.spec.ts`           | CLI Usage sidesheet opens, all three CLIs present (Copilot/Claude/Codex), version pills shown, no error banner. |
| `quota.spec.ts`               | Claude quota probe is reachable and reports enough headroom to start a task. |
| `add-task.spec.ts`            | Add Task dialog opens, agent + model selection works for Claude. |
| `claude-hello-world.spec.ts`  | Full-loop smoke test: create → start → wait → assert clean Claude Code result for a trivial Hello World prompt. Marked `@billable` because it consumes real Claude quota. |

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

## Authoring guidelines

- One spec = one user-visible feature. Keep specs small.
- No hardcoded waits (`waitForTimeout`). Use `expect.poll` or web-first
  assertions.
- Always clean up jobs you create — leftover jobs pollute the dev board.
- Tag long/expensive specs with `@billable` so CI can opt out.
