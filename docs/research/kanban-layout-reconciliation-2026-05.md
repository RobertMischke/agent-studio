# Kanban layout reconciliation, 2026-05

This is the reconciliation pass that runs after `kanban-board-design-spec-mockup-first` locked the spec at `docs/mockups/kanban-board-design/`. It reviews each of the seven competing layout tasks against that spec, names the mismatches, and proposes the smallest follow-up patch each task needs. No auto-merge; the user picks which patches land and when.

The spec itself ships its own `reconciliation.md` next to the taxonomy. This document is the second pass: tighter, less editorial, focused on the smallest viable change per task and on the actual on-disk state of those tasks today.

## Lane-name correction

The original prompt for this reconciliation referenced `4-review` and `6-archive`. Those names predate ADR-0025 (`lanes-with-explicit-human-review-step`), which split review into `4-auto-review` and `5-human-review` and renumbered completed and archive to `6-completed` and `7-archive`. The spec uses the new vocabulary. Every reference below uses the post-ADR names.

## Task-by-task notes

### 1. vscode-style-layout-density-and-tabs (currently in `4-auto-review`)

Delivered the `Frontend:VsCodeLayout` flag, an activity bar, a status bar, a denser detail header, and the chrome-density tables (13 px chrome, 14 px body, hairline borders) that the kanban spec borrows verbatim. It owns density, not the kanban grid math, and never declared which numbers apply to the board itself. Conflict with the spec: implicit; this task did not say "the kanban board uses these numbers," so the kanban grew its own flex layout that drifted from the editor-tab math. Smallest follow-up patch: append one paragraph to `docs/mockups/vscode-layout/taxonomy.md` saying "Kanban-specific decisions live in `kanban-board-design/taxonomy.md`; this file does not duplicate them," and add a back-link from this task's prompt. No CSS change; the flag remains independent.

### 2. kanban-lane-grouping-collapse (currently in `4-auto-review`)

Delivered the collapsed-rail rendering, indicator dots (running, needs-input, error, CLI badge), drag-and-drop into the rail, and per-lane localStorage persistence. Conflicts: rail width is 36 px (spec locks 48 px, matching VS Code's activity bar carry-over); collapse persistence uses a global key (spec requires `atp.kanban.collapsed.<project>`); default collapse state is "all expanded" (spec wants `7-archive` collapsed for new projects). Smallest follow-up patch: bump rail width in `frontend/src/app/components/job-column.ts`, switch the localStorage key shape and write a one-time read-from-legacy-then-drop migration on boot, and seed `7-archive` collapsed when no per-project entry exists. The Playwright spec at `frontend/e2e/kanban-lane-grouping.spec.ts` needs the rail-width assertion updated.

### 3. expanded-lifecycle-lanes-concept (currently in `4-auto-review`)

Delivered the seven-lane vocabulary research and the choice between filesystem states, virtual lanes, sidecar substates, and a hybrid model. The spec adopts this vocabulary and cites this task as the authority. No visual conflicts. Smallest follow-up patch: none. Mark the task reconciled and let it ride to `5-human-review`.

### 4. lanes-with-explicit-human-review-step (currently in `4-auto-review`)

Delivered ADR-0025: split `4-review` into `4-auto-review` plus `5-human-review`, renumbered the lanes, wrote the boot-time migration in `JobStateMachine`, routed orchestrator decisions to the new lane, and added the seven-lane Playwright spec. Conflict: the original mockup proposed inline background tints to distinguish auto-review from human-review lane headers; the spec forbids any background fill on lane headers and reserves color for 1 px outline tints only. The robot and eye icons survive; only the fill rule changes. Smallest follow-up patch: replace any `background-color` on `.column__header` for these two lanes with a 1 px outline using `--accent-blue` for the active phase, drop the fill, keep the icons. Open caveat from this task's status: full test verification was blocked by a backend DLL lock; the user still needs to restart the dev backend to confirm the migration count.

### 5. fix-kanban-lane-layout-overflow (currently in `2-ready`, queued at position 1)

Delivered the regression diagnosis (six lanes no longer fit at 1440x900), planned a Playwright spec asserting zero horizontal overflow with all lanes visible, and proposed `flex: 1 1 0` with a 200 px min-width. Conflicts: the spec locks `grid-template-columns: repeat(N, minmax(220px, 1fr))` instead of flex; the 220 px floor is 20 px above the proposed 200 px; and the original "six lanes" assertion target is now seven lanes (or eight when `failed-pickup` is on). Smallest follow-up patch: rewrite the proposed fix as "implement the locked grid template" rather than "tune flex math," update the Playwright assertion target to match `grid-template-columns: /repeat\(7,/`, bump the min-track to 220 px, and confirm overflow stays `auto` as graceful fallback. Do this before the task gets picked up again, so the agent does not redo flex work that the spec already overrides.

### 6. chat-layout-integration-bridge (currently in `2-ready`)

Delivered the `Frontend:NextGenChat` flag scaffold, the `ConversationEvent` contract draft, projection fixtures, and a host-inventory list of preserved surfaces. Touches the chat workbench, not the board. Indirect interaction: the project side sheet shares the dashboard viewport, and the board's locked 16 px outer padding must not move when the side sheet expands. Smallest follow-up patch: add a one-line note to this task's prompt that the side sheet does not change board padding, and that any new chat host must measure its width from the dashboard right edge rather than from the lane-row container.

### 7. improve-layout-of-the-details-page (currently in `2-ready`)

Delivered the detail-page header refinements that defer to `vscode-layout/taxonomy.md`. Touches the task-detail surface, not the board. No conflicts on the kanban layer. Smallest follow-up patch: none. The detail page inherits density from `vscode-layout`, not from the kanban spec, and the spec is silent about it on purpose.

## Summary table

| Task slug                                | Lane today      | Conflict?       | Smallest patch                                                                     |
|------------------------------------------|-----------------|-----------------|------------------------------------------------------------------------------------|
| vscode-style-layout-density-and-tabs     | 4-auto-review   | implicit only   | one-paragraph delegation in `vscode-layout/taxonomy.md`                            |
| kanban-lane-grouping-collapse            | 4-auto-review   | width, default  | rail 36 to 48 px; per-project key with legacy migration; archive default-collapsed |
| expanded-lifecycle-lanes-concept         | 4-auto-review   | none            | none                                                                               |
| lanes-with-explicit-human-review-step    | 4-auto-review   | color rule      | drop background fill on lane headers; keep robot and eye icons                     |
| fix-kanban-lane-layout-overflow          | 2-ready (1)     | grid recipe     | use `repeat(N, minmax(220px, 1fr))`; assert seven lanes; min-track 220 px           |
| chat-layout-integration-bridge           | 2-ready         | none            | one-line note: side sheet does not change board padding                            |
| improve-layout-of-the-details-page       | 2-ready         | none            | none                                                                               |

## Operational notes from this pass

### Duplicate folder in `7-archive`

`kanban-lane-grouping-collapse-empty-2026-05-05` exists in `7-archive` next to the live `kanban-lane-grouping-collapse` folder in `4-auto-review`. The duplicate has only a `results/` subfolder with two PNG files (`kanban-board-collapsed.png`, `kanban-board-expanded.png`) created at 12:44:18 on 2026-05-05. It carries no `job.json`, no `prompt.md`, no `logs/`, no `status.md`. The two PNGs were captured 53 seconds after the same-named files in the live folder; they share the filename but the bytes differ, so this was a separate brief capture, not a copy. Conclusion: the duplicate is a half-formed shell created by the boot-archive sweep while the real folder was active in `4-auto-review`. Safe to delete.

The API path for deleting it is `DELETE /api/jobs/{jobId}` with the project's `watchPath`, but it returned 404 for this folder. Reason: `JobScannerService.ScanJobFolder` requires a `job.json` to recognise a folder as a job, and the duplicate has none. The folder is below the API's discovery surface, so a clean API-only deletion is not currently possible. Two ways forward, user picks: (a) the user deletes the folder by hand from `7-archive` (lowest risk; filesystem-direct mutation but reversible from `git`-equivalent backups since the workspace is not under version control); or (b) extend `JobStateMachine.DeleteJob` to admit a folder-without-job.json path under `7-archive` only, which would let the API clean up boot-sweep residue end-to-end. This task did not act unilaterally on the filesystem.

The boot-archive sweep bug is reproducible from this evidence. The sweep saw a `kanban-lane-grouping-collapse` slug, intended to archive a stale folder by that name, and produced a `<slug>-empty-<date>` folder under `7-archive` while the live folder was simultaneously sitting in `4-auto-review`. The sweep must check whether a folder with the same slug exists in any other lane before creating an archive copy. If a sibling exists in any non-archive lane, the sweep should either skip the archive entirely or raise a `[supervisor]` chat-note instead of silently producing the duplicate. The check belongs in `StaleProgressArchiver` (or whichever service owns the boot sweep); a slug-presence query against `JobScannerService` answers it with one call.

### Long-running fix-kanban-lane-layout-overflow verdict

This task was flagged as "currently active; 50 min runtime is suspect" in the original prompt for this reconciliation. That snapshot is stale. The actual runtime telemetry from `logs/cli-output.log`:

- Claude CLI exited at 13:00:18 with `status=stopped, exitCode=-1, duration=906.6s` (about 15 min).
- The orchestrator heuristic at 13:00:19 logged "Could not classify the agent's reply."
- The user sent a continue at 13:33:57, the orchestrator marked the project busy, and the task was moved back to `2-ready` at position 1.
- `lastProgressAt` in `job.json` is 13:00:19; the folder has been quiet since.

So the task is not stuck mid-run; it is queued. No supervisor force-fail or extended-watch is needed. If the next pickup runs into the same 15 min ceiling, that is the time to escalate. Recommendation: let the queue replay it once. If the next attempt also exits with `status=stopped`, write a real `[supervisor]` chat-note then.

## Re-issue resolution, 2026-05-14

The reconciliation was reissued in `5-human-review` on 2026-05-11 with two open items:

1. Duplicate `kanban-lane-grouping-collapse-empty-2026-05-05` in `7-archive` still present (DELETE returned 404 because `JobScannerService` requires a `job.json`).
2. Lane naming: the prompt referenced the pre-ADR-0025 lanes (`4-review`, `6-archive`).

### Item 1: duplicate folder

Status today: the orphan folder is still on disk and the API still cannot reach it. Per the durable feedback rule that the LLM may not mutate job folders directly, this run did not delete the folder. The systemic fix already has a queued backlog task:

- `bug-stale-archiver-creates-phantoms-on-backend-restart-mid-move` (currently in `0-backlog`).

That task's solution sketch already covers the same root cause this reconciliation called out: a pre-sweep cross-lane reconciliation in `StaleProgressArchiver` that silently drops a `3-progress` (or otherwise-staged) folder when an intact same-slug folder exists in a later lane. Its acceptance criteria match what is needed to stop new orphans of this shape. Backfill cleanup of folders that already exist is explicitly listed as out-of-scope in that task ("manueller cleanup-Pfad, kein Backend-Job").

What this means for the current orphan:

- The systemic fix is tracked. No new follow-up task is required.
- The current orphan needs the user to either run the manual delete on the filesystem or, if they would prefer an API-only path, extend `JobStateMachine.DeleteJob` to admit a `7-archive` folder without `job.json`. Both options remain documented in the "Duplicate folder in 7-archive" section above; this run made no change to either.

### Item 2: lane naming

Already addressed inline in this document. The "Lane-name correction" section at the top maps `4-review` to `4-auto-review` / `5-human-review` and `6-archive` to `6-completed` / `7-archive`, and every task-by-task entry below it uses the post-ADR names. No further patch is required for this item.

### Lane drift since the original reconciliation

The seven layout tasks have moved on. As of 2026-05-14:

| Task                                     | Lane in original doc | Lane today        |
|------------------------------------------|----------------------|-------------------|
| vscode-style-layout-density-and-tabs     | 4-auto-review        | 7-archive         |
| kanban-lane-grouping-collapse            | 4-auto-review        | 7-archive         |
| expanded-lifecycle-lanes-concept         | 4-auto-review        | 7-archive         |
| lanes-with-explicit-human-review-step    | 4-auto-review        | 2-ready (reissue) |
| fix-kanban-lane-layout-overflow          | 2-ready (1)          | 7-archive         |
| chat-layout-integration-bridge           | 2-ready              | 7-archive         |
| improve-layout-of-the-details-page       | 2-ready              | 7-archive         |

The body of this document is preserved as-is; the lane column in the per-task headers reflects the state at the time of the original pass, not today. The reconciliation verdicts themselves still stand; only the on-board lane has shifted.

### Verdict on `fix-kanban-lane-layout-overflow`, refreshed

The original verdict ("queued, not stuck; let the queue replay it once") played out as expected. The task is now in `7-archive`. No supervisor action needed; the recommendation is closed.

## Re-run update, 2026-06-01

The task fired again. Re-checked state and corrected the one weak spot from the 2026-05-14 pass.

### Orphan folder: dedicated follow-up task created

The 2026-05-14 note said "no new follow-up task is required" and leaned on `bug-stale-archiver-creates-phantoms-on-backend-restart-mid-move` to cover the orphan. That was not enough: that task prevents NEW orphans but explicitly scopes OUT backfill cleanup of folders that already exist, and it does not touch the deletion API. So the existing orphan had no tracked owner and no actionable path.

Verified the gap still stands today:

- The orphan `kanban-lane-grouping-collapse-empty-2026-05-05` is still on disk in `7-archive`.
- `TaskStateMachine.DeleteJob` (`backend/Services/Jobs/TaskStateMachine.cs:378`, formerly `JobStateMachine.DeleteJob`) resolves the folder via `_scanner.FindJob`, which requires `job.json`. The orphan has none, so `DELETE /api/tasks/{id}` still returns 404. Implementing that extension is out of scope for this paperwork-only reconciliation task.

Resolution: created a dedicated follow-up task via the API rather than leaving the item open:

- `extend-deletejob-to-remove-jobjson-less-orphan-folders` (created in `0-backlog`, project agent-taskboard).

It specifies extending the delete path to admit a `job.json`-less folder under terminal lanes only (hard-gated so it can never touch a real task or a non-terminal lane), exposing it on the API, using it to remove this specific orphan, and a test for both the allow and refuse cases. The phantom-prevention task remains the complement: it stops new orphans, this one removes existing residue.

### State changes since the last pass

- `bug-stale-archiver-creates-phantoms-on-backend-restart-mid-move` advanced from `0-backlog` to `2-ready` (the systemic prevention fix is now queued).
- `lanes-with-explicit-human-review-step` moved from `2-ready` to `7-archive` (its reissue completed).
- The API job routes were renamed from `/api/jobs` to `/api/tasks`; all mutations in this run used `/api/tasks`.

### Footers

All seven affected task prompts already carry the `Reconciled against spec on 2026-05-05` footer from the original pass; the reconciliation verdicts have not changed, so the footers were left as-is rather than re-dated.
