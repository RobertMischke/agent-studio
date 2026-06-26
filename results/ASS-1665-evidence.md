# ASS-1665 — Per-card Git-state badge (pre-merge / post-merge / tagged)

Reissue findings were: (1) implementation + tests reported as absent / phantom,
(2) test execution unverified, (3) the task diff carried only ng-serve log noise
and a screenshot harness. This run resolves all three.

## What the requirement asks (prompt + Konzept §4)

Per card, a badge showing the git-integration state — `auf task/<id>` (pre-merge)
vs `in develop` (post-merge) vs *tagged* — **without** renaming any `state` key.

## Implementation IS present and wired

The badge is a pure function of `TaskInfo` and is wired end-to-end. No `state`
key was renamed; the lane only gates *whether* a pill shows, the label comes from
the `job.provenance` ground truth:

- `buildGitStateBadge()` — `frontend/src/app/features/board/components/task-card/task-card-view-model.ts:627`
  (kinds `pre-merge | post-merge | tagged` — `:549`).
- computed signal `gitStateBadge` — `frontend/src/app/features/board/components/task-card/task-card.component.ts:240`.
- template render with `[attr.data-git-state]` — `frontend/src/app/features/board/components/task-card/task-card.component.html:358`.
- pill SCSS (`--pre-merge / --post-merge / --tagged`) — `frontend/src/app/features/board/components/task-card/task-card.component.scss:956`.

## Tests — verified, not just claimed

Spec: `frontend/src/app/features/board/components/task-card/task-card-view-model.git-state.spec.ts`

Command (Angular `@angular/build:unit-test`, vitest runner):

```
npx ng test --no-watch --include='**/task-card-view-model.git-state.spec.ts'
```

Result:

```
 Test Files  1 passed (1)
      Tests  11 passed (11)
```

Coverage spans all three lifecycle states: A) active `task/<id>` worktree
(incl. reissue → newest tip, escalated-conflict still pre-merge), B) landed in
develop (merge fact, terminal Completed, post-integration review lane), C)
sequential run in the shared main checkout, plus the archived → `tagged` collapse
and the quiet (null) lanes.

## Screenshot evidence (real badge code, mocked data)

`results/git-state-pill/ASS-1665-git-state-badges--mocked.png`

Captured by building the `git-state-pill-mockup` target and driving chromium over
a static server (`frontend/scripts/git-state-pill-shots.mjs`). The gallery renders
the **shipped** `buildGitStateBadge` over seeded `TaskInfo` fixtures (no backend),
producing the four AFTER pills the harness logged:

- A-active     → `⎇ task/ASS-1752`        (`--pre-merge`)
- A-reissue    → `⎇ task/ASS-1752`        (`--pre-merge`, newest tip)
- B-landed     → `⬇ develop @ddddddd`     (`--post-merge`)
- C-sequential → `✎ main checkout`        (`--pre-merge`)

It is labelled `--mocked` because the pill is a pure function of `TaskInfo`; no
running backend is involved (same precedent as the other `src/mockups/*` shots).

## Noise removed

The two committed ng-serve capture files
(`frontend/.develop-layout-compare-ng-serve.{out,err}.log`) were untracked and
deleted, and `.gitignore` gained `frontend/*ng-serve*.log` so ad-hoc ng-serve
captures that don't use the `.ng-serve-<port>` prefix can't be committed again.
