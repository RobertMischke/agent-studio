# Task Detail Overview

## Purpose

The overview tab is the first review surface for a task. It shows status,
agent configuration, project identity, references, pipeline progress, and the
current review posture in one place.

![Task detail overview](../../../assets/images/detail-overview--pinned.png)

## Relevant State

- Manifest id: `task-detail-overview`
- Route: `/tasks/DEMO-9`
- Viewport: `1440x900`
- Data state: pinned DEMO-9 task selected from the live board
- Visible state: Overview tab, task status, agent block, references, and pipeline

## How To Recreate The Screenshot

```sh
./scripts/visual-docs/generate.sh
```

The Playwright spec opens the board, selects a preferred pinned DEMO-9 task such as
`ASS-847`, waits for `overview-tab`, and captures
`docs/assets/images/detail-overview--pinned.png`.

## Marketing Usage

Use this image when the website needs to show that a task is more than a CLI
run: it is a structured review object with status, configuration, and pipeline
context.
