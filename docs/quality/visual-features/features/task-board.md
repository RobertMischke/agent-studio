# Task Board

## Purpose

The task board shows the work that agents can do, are doing, or have already
handed back for review. It turns agent activity into visible tasks instead of a
private terminal stream.

![Agent Studio board overview](../../../assets/images/board-overview--pinned.png)

The landing-page variant keeps the same pinned state at 4K and renders three
capture annotations directly in Playwright:

![Landing-page board hero](../../../assets/images/presentation/07-landing-board-hero--dark--pinned.png)

## Relevant State

- Route: `/`
- Viewport: `1440x900`
- Data state: pinned Demo App workspace
- Visible state: project tree, lanes, job cards, runner state, and usage summary

This state matters because it is the first visual proof of the product: tasks
are grouped by lifecycle, and the human can see what needs attention.

## How To Recreate The Screenshot

Manifest id: `task-board-overview`

Landing manifest id: `landing-board-hero`

```sh
./scripts/visual-docs/generate.sh
```

The Playwright spec:

1. Opens `/`.
2. Waits for the DEMO-9 card.
3. Uses the pinned Demo App workspace data.
4. Captures `docs/assets/images/board-overview--pinned.png`.

## Marketing Usage

This image is used as `homeBoardWorkbench` in the marketing site. It should
support copy about Agent Studio as a visible workbench for reviewable
agent-task work.
