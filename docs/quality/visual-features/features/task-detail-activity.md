# Task Detail Activity

## Purpose

The Activity tab shows the operational side of a task: trace, conversation,
run activity, and the follow-up composer. It is where a human can inspect what
happened and steer the next step.

![Task detail activity tab](../../../assets/images/detail-activity--pinned.png)

## Relevant State

- Manifest id: `task-detail-activity`
- Route: `/tasks/DEMO-9`
- Viewport: `1440x900`
- Data state: pinned DEMO-9 task selected from the live board
- Visible state: Activity tab in the protocol pane

## How To Recreate The Screenshot

```sh
./scripts/visual-docs/generate.sh
```

The Playwright spec opens a pinned DEMO-9 task, clicks `inspector-tab-activity`,
waits for `activity-panel`, and captures `docs/assets/images/detail-activity--pinned.png`.

## Marketing Usage

Use this image for the Plus-One/Plus-N developer pattern and for copy about
human steering over agent work.
