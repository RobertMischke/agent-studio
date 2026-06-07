# Task Detail Timeline

## Purpose

The Timeline tab makes task progression inspectable. Events, verdicts, re-open
loops, and operator-visible progress are shown chronologically instead of being
left in scattered terminal output.

![Task detail timeline tab](../../images/detail-timeline.png)

## Relevant State

- Manifest id: `task-detail-timeline`
- Route: `/tasks/<existing-agent-studio-task>`
- Viewport: `1440x900`
- Data state: existing Agent Software Studio task selected from the live board
- Visible state: Timeline tab with task events or the timeline empty state

## How To Recreate The Screenshot

```sh
./scripts/visual-docs/generate.sh
```

The Playwright spec opens an existing task, clicks `prompt-tab-timeline`, waits
for `timeline-tab`, and captures `docs/images/detail-timeline.png`.

## Marketing Usage

Use this image to support claims about reviewable task progression and
decision history.
