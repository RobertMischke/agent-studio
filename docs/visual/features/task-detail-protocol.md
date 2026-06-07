# Task Detail Protocol

## Purpose

The task detail view shows one unit of work. The protocol pane explains what
happened, what evidence exists, and why the task is ready for review.

![Task detail with protocol pane](../../images/detail-protocol.png)

## Relevant State

- Route: `/tasks/<existing-agent-studio-task>`
- Viewport: `1440x900`
- Data state: existing Agent Software Studio task selected by the Playwright spec
- Visible state: task prompt and protocol pane are visible together

This state matters because a reviewer should not have to reconstruct task
history from a terminal transcript. The protocol pane keeps the review story
near the task.

## How To Recreate The Screenshot

Manifest id: `task-detail-protocol`

```sh
./scripts/visual-docs/generate.sh
```

The Playwright spec:

1. Opens `/`.
2. Opens the preferred existing task card.
3. Waits for `pane-protocol`.
4. Captures `docs/images/detail-protocol.png`.

## Marketing Usage

This image is available as `taskDetailProtocol` for Visual Documentation
Library copy. It should be used when the site needs to show that a screenshot
can carry route, state, and reproduction metadata.
