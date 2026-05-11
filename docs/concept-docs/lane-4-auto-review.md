# Auto-Review

Auto-review is the orchestrator's lane (the machine-icon column on the board). When an agent run ends with `[[TASK_DONE]]` the runner moves the job from `3-progress` into `4-auto-review` and the `ReviewDecisionOrchestrator` takes over. The lane is intentionally narrow in scope: it decides whether a finished run is good enough to put in front of you, and nothing more. It is not a checkpoint where humans re-confirm; that role belongs to `5-human-review` next door.

## What the orchestrator does here

For every job sitting in this lane, the orchestrator runs a short multi-aspect quality pass. Each aspect is a separate fast-model call against a tightly scoped slice of the evidence (requirement fit, code quality, documentation impact, tests and evidence). Each call writes its own `aspect-*.md` into the job folder so the result is replayable and inspectable. The orchestrator then folds the per-aspect verdicts into one decision and acts on it deterministically. The agent's own self-report is one input among several; the lane never blindly trusts a `[[TASK_DONE]]`.

## Three outcomes

- **All aspects pass.** The job is promoted to `5-human-review` with no flags. You see a clean card and decide whether to accept it.
- **Some concerns.** The job is promoted to `5-human-review` carrying a small warning chip on the card. The aspect markdowns spell out what was uneasy.
- **Any aspect blocks.** The job is reissued back to `3-progress` with a follow-up summarising the findings. This is how the orchestrator catches "looks done, isn't done" outcomes without bothering you.

## Diff discovery: full job range, not HEAD alone

Each aspect prompt is fed a diff summary that names the commits the job produced. The summary is built from **every commit attributed to the job across all of its runs** (via the run timeline's `HeadShaBefore..HeadShaAfter` SHA ranges, deduped) plus the auto-commit recorded on `JobInfo.Commit`. This is the same aggregation pipeline that powers `/api/jobs/{id}/commits` in the protocol pane, so the reviewer and the human see the same set of commits.

The lane explicitly does not look at HEAD alone. Crash-recovery commits land as near-empty fixups on top of the real work; if the aspect runner only saw HEAD it would report "0 files changed" and false-positive a block on a successful refactor. Walking the full run range avoids that drift.

When the aggregate is genuinely empty — no run-window range produced commits and no auto-commit was recorded — the summary states this explicitly ("No commits attributed to this task") rather than emitting a misleading "Files changed: 0".

## What to do when the lane stalls

If a job sits in this lane longer than feels right: the multi-aspect pass is sequential, so a slow CLI quota or a stuck fast-model call can hold the whole lane up. The header shows the live status line ("Reviewing X. Last tick: A accept, B reissue, C escalate"). When that line stops updating for many minutes, glance at `logs/meta/<project>/observations.jsonl` for advisories or use the supervisor's pause/resume primitives to nudge it. The kill switch for the multi-aspect pass is `ReviewDecisionOrchestrator:AspectsEnabled`; flipping it off makes auto-review a passthrough.

## Reference

- ADR-0025 (three-stage review pipeline)
- ADR-0026 (multi-aspect orchestrator review)
