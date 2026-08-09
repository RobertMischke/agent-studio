# Task Detail Docs

## Purpose

The Docs tab keeps task-local documents attached to the work while putting their
topics and conclusions first. Code reviews, verdicts, notes, and generated
Markdown or JSON reports open as readable documents before prompts and raw
artifacts. Technical file metadata remains available from each document menu.

![Task detail Docs tab](../../../assets/images/detail-files--pinned.png)

## Relevant State

- Manifest id: `task-detail-files`
- Route: `/tasks/DEMO-9`
- Viewport: `1440x900`
- Data state: pinned DEMO-9 task selected from the live board
- Visible state: Docs tab with rendered outcome documents before source artifacts

## How To Recreate The Screenshot

```sh
./scripts/visual-docs/generate.sh
```

The Playwright spec opens a pinned DEMO-9 task, clicks `prompt-tab-description`,
waits for `files-pane`, and captures `docs/assets/images/detail-files--pinned.png`.

## Marketing Usage

Use this image to explain that Agent Studio keeps generated outcomes readable,
navigable, and inspectable as part of the task record.
