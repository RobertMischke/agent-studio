# Task Board

## Purpose

The task board shows the work that agents can do, are doing, or have already
handed back for review. It turns agent activity into visible tasks instead of a
private terminal stream.

![Agent Task Processor board overview](../../images/board-overview.png)

## Relevant State

- Route: `/`
- Viewport: `1440x900`
- Data state: existing `Sample Shop` demo workspace
- Visible state: project tree, lanes, job cards, runner state, and usage summary

This state matters because it is the first visual proof of the product: tasks
are grouped by lifecycle, and the human can see what needs attention.

## How To Recreate The Screenshot

Manifest id: `task-board-overview`

```sh
cd frontend
PW_TARGET=dev npx playwright test e2e/visual-evidence/readme-screenshots.spec.ts --project=chromium
```

The Playwright spec:

1. Checks that `/api/watch-paths` contains a watch path named `Sample Shop`.
2. Opens `/`.
3. Waits for the first `job-card`.
4. Captures `docs/images/board-overview.png`.

## Marketing Usage

This image is used as `homeBoardWorkbench` in the marketing site. It should
support copy about Agent Studio as a visible workbench for reviewable
agent-task work.
