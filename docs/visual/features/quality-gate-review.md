# Quality Gate Review

## Purpose

The quality-gate view shows that review is part of the task lifecycle. A task
can carry implementation evidence, protocol state, Git evidence, and quality
context before a human accepts or redirects it.

![Quality-gate task detail](../../images/detail-quality-gate.png)

## Relevant State

- Route: `/tasks/<sample-shop-quality-task>`
- Viewport: `1440x900`
- Data state: existing `Sample Shop` task selected by visible title text
- Visible state: protocol and quality-gate context are visible in the task detail

This state matters because quality checks should be evidence, not a private
claim from the agent.

## How To Recreate The Screenshot

Manifest id: `quality-gate-review`

```sh
cd frontend
PW_TARGET=dev npx playwright test e2e/visual-evidence/readme-screenshots.spec.ts --project=chromium
```

The Playwright spec:

1. Returns to the board.
2. Clicks the first `job-card` containing `wishlist`.
3. Waits for `pane-protocol`.
4. Captures `docs/images/detail-quality-gate.png`.

## Marketing Usage

This image is used as `taskDetailQualityGate` in the marketing site. It should
support copy about quality gates, review evidence, and Angular Quality Rails.
