# Task Detail With Evidence Panes

## Purpose

The three-pane task detail view shows prompt, protocol, and Git evidence at the
same time. It is the review posture of the product: the human can inspect the
request, the result, and the repository evidence without losing the task.

![Task detail with prompt, protocol, and Git panes](../../images/detail-three-panes.png)

## Relevant State

- Route: `/tasks/<sample-shop-task>`
- Viewport: `1440x900`
- Data state: existing `Sample Shop` task selected by visible title text
- Visible state: prompt pane, protocol pane, and Git pane are visible

This state matters because review is not only a yes/no decision. It is a small
investigation across task intent, run summary, and code evidence.

## How To Recreate The Screenshot

Manifest id: `task-detail-three-panes`

```sh
cd frontend
PW_TARGET=dev npx playwright test e2e/visual-evidence/readme-screenshots.spec.ts --project=chromium
```

The Playwright spec:

1. Opens `/`.
2. Clicks the first `job-card` containing `coffee`.
3. Waits for `pane-protocol`.
4. Clicks `pane-toggle-git`.
5. Waits for `pane-git`.
6. Captures `docs/images/detail-three-panes.png`.

## Marketing Usage

This image is used as `reviewTaskEvidenceGit` in the marketing site. It should
support claims about reviewable work, evidence, Git context, and the
Plus-One/Plus-N developer pattern.
