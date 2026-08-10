# Task Detail Git Focus

## Purpose

The Git focus state shows the code evidence directly: commits, changed files,
and file tree context. Review can temporarily focus on repository facts without
losing the task.

![Task detail Git focus](../../../assets/images/detail-git-focus--pinned.png)

## Relevant State

- Manifest id: `task-detail-git-focus`
- Route: `/tasks/DEMO-9`
- Viewport: `1440x900`
- Data state: pinned DEMO-9 task selected from the live board
- Visible state: Git pane is visible while prompt and protocol panes are hidden

## How To Recreate The Screenshot

```sh
./scripts/visual-docs/generate.sh
```

The Playwright spec opens a pinned DEMO-9 task, enables the Git pane, hides prompt
and protocol panes, and captures `docs/assets/images/detail-git-focus--pinned.png`.

## Marketing Usage

Use this image when the website needs to show that Agent Studio makes concrete
commit evidence inspectable.
