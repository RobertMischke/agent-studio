# System Review Skill (Layer 3)

A read-only review skill for the running stable instance of Agent Software Studio. Driven from outside the app. The user (or a scheduler) runs this every 4-8 hours; it produces one structured review file per run.

This is the **third loop** above the orchestrator: not part of the app's runtime, so it survives any failure mode of the app itself.

## What this skill does NOT do

- Does not modify the watched stable checkout.
- Does not write inside any project's source tree.
- Does not contact external services.
- Does not invoke or restart the app.

The only side effect is writing one Markdown file under `logs/system-review/<date>-<time>.md` in the watched workspace.

## Inputs the skill must read

Set the workspace root via the `ATP_WORKSPACE` env var; defaults to `C:\Projects\agent-taskboard-workspace`. From there:

1. **Projects index**: `<workspace>/projects/`. List every project; for each, list lane folders `1-preparation` ... `6-archive`.
2. **Job state per project**: each `<project>/<lane>/<job>/job.json` (id, title, state, agent, cliType, model, createdAt). Optionally `prompt.md`, `status.md`.
3. **Activity logs per active job**: `<project>/<lane>/<job>/logs/cli-output.log`. Tail ~200 lines for "what is the agent saying right now" signal.
4. **Supervisor logs (when present)**: `<workspace>/logs/meta/<project>/observations.jsonl`, `interventions.jsonl`, `reasoning.md`, `heartbeat.json`. Layer 3 reads these even though it is independent of Layer 2; if Layer 2 is dead the heartbeat will be stale and the review must say so.
5. **Recent commits in the stable repo**: `git -C <stable-checkout> log --since="$LOOKBACK"` (default lookback 8 hours). Confirms the orchestrator actually shipped work, not just churn.
6. **Backend crash marker**: `<stable-checkout>/logs/backend/last-crash.json`. The backend's process-wide exception handlers write this when an `UnobservedTaskException` or `UnhandledException` fires. When present, it carries `capturedAt`, `source`, `exceptionType`, `message`, and `topFrame`. If the file is fresh (within the lookback window) the review must surface it under "Anomalies" with the captured fields and pointer the operator at the matching daily log file `<stable-checkout>/logs/backend/<date>.log` for the full stack. The same data is served by `GET http://127.0.0.1:5030/api/diagnostics/last-crash` when the backend is up.
7. **Previous system review files**: `<workspace>/logs/system-review/`. Last review's open items are followed up in this review.

If a path does not exist, note it in the review and continue. Never fail the run on a missing path.

## Questions every review must answer

For the lookback window (default last 8 hours):

1. **Throughput.** How many jobs moved into `4-review` and `5-completed`? How many failed and stayed in `3-progress`? Compare against the previous review to spot regressions.
2. **Stuck work.** Any task in `3-progress` for longer than its previous run took, or longer than a configurable threshold (default 30 min)? List with names and last-progress timestamps.
3. **Failure patterns.** Same task retried more than once? Same error class across multiple tasks? Same CLI repeatedly failing on the same project?
4. **Quota burn.** Per-CLI quota usage where logs expose it. Project the next reset; flag if a CLI is likely to exhaust before reset.
5. **Supervisor health.** For each project with supervisor logs: heartbeat fresh (last < 60 s)? Recent advisories (count by severity)? Any interventions fired? Were the interventions effective (did the situation improve or recur)?
6. **Findings ignored.** Advisories with severity Warn or High that have no follow-up task or acknowledgement after N hours. These are the highest-leverage items for the next review.
7. **Commit shape.** Stable's commit log over the lookback: are commits landing? Any push without a prior task? Any task without a corresponding commit?
8. **Anomalies.** Anything that deviates from the project's pattern: unusually long run, unusually short success, unexpected agent type swap, stop/restart of the backend.
9. **Verdict.** Plain prose: is the system behaving correctly? One paragraph. The user reads this first.

## Output

Write to `<workspace>/logs/system-review/<YYYY-MM-DD-HHmm>.md`:

```markdown
# System Review - <date> <time>

**Verdict.** One paragraph. Healthy / Caution / Action needed.

## Throughput
- Jobs moved to 4-review: N
- Jobs moved to 5-completed: N
- Jobs that failed and stayed in 3-progress: N
- Net delta vs previous review: ...

## Stuck work
- ...

## Failure patterns
- ...

## Quota burn
- ...

## Supervisor health
- ...

## Findings ignored
- ...

## Commits
- ...

## Anomalies
- ...

## Open items for next review
- ... (carried forward)
```

Keep the report tight: bullets over prose. The verdict paragraph is the only narrative section.

## Cadence

- Default: every 4-8 hours, run manually by the user.
- Scheduling support is a later phase; keep this skill stand-alone for now.
- Each review references the previous review's open items so the carry-forward chain is visible.

## Failure modes

- **Stable checkout missing.** The script defaults to `C:\Projects\agent-taskboard-devspace\agent-taskboard-stable`; if that path does not exist, write a review file that says so and stop. Do not guess.
- **Workspace empty.** If `<workspace>/projects/` has no entries, write a review noting "no projects watched yet" and stop.
- **Concurrent run.** If a review with the same minute timestamp already exists, append a `-2` suffix; do not overwrite.

## Reporting back to the orchestrator

This skill is independent of the supervisor and of the orchestrator. It does not write into the orchestrator's chat log. Its only output is the Markdown review file. Future versions may surface the latest review as a banner on the app's home screen, but that integration is not part of this skill.
