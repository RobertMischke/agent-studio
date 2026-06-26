# System Review Skill (Layer 3)

A read-only review skill for the running stable instance of Agent Software Studio. Driven from outside the app. The user (or a scheduler) runs this every 4-8 hours; it produces one structured review file per run.

This is the **third loop** above the orchestrator: not part of the app's runtime, so it survives any failure mode of the app itself.

## What this skill does NOT do

- Does not modify the watched stable checkout.
- Does not write inside any project's source tree.
- Does not contact external services.
- Does not invoke or restart the app.
- Does not move jobs, force-fail runs, or post to the Agent Message Bus.

The only side effect is writing one Markdown file under `logs/system-review/<date>-<time>.md` in the watched workspace, plus optionally one structured JSON sidecar with the same basename when the operator wants a machine-readable copy.

## Inputs the skill must read

Set the workspace root via the `ATP_WORKSPACE` env var; defaults to `C:\Projects\agent-taskboard-workspace`. From there, **read the Agent Message Bus first** and only fall back to the legacy raw streams when the bus is missing or empty:

1. **Agent Message Bus (primary).** `<workspace>/logs/bus/`. Layout per [docs/architecture/bus/agent-message-bus.md](../../docs/architecture/bus/agent-message-bus.md) section 4:
   - `participants/<id>.json` - one JSON document per participant. Use this to resolve `participantId` -> `cli` / `skill` / `kind` so filters can pivot.
   - `_workspace/<yyyy-mm-dd>.jsonl` - workspace-wide messages (orchestrator-global, runtime startup).
   - `<project>/<yyyy-mm-dd>.jsonl` - per-project messages, one `AgentMessage` per line (schema: [`agent-message.schema.json`](../../docs/schemas/agent-message.schema.json)).
   Sort messages by `id` (ULID / UUID v7 lexical order matches creation time). Filter to the lookback window via `createdAt`. Every finding produced from the bus must reference the `id`, plus `project`, `jobId`, `runId`, `artifacts[].uri` when present.
2. **Bus exported as JSONL fixtures (alternate).** When the full bus store is not yet available on the host (offline analysis, post-incident export, integration tests), the skill accepts a single hand-built or exported JSONL file via the dry-run path (see "Dry-run mode" below). The fixture is shaped exactly like a bus day-file; the skill code does not branch on the source. This is the documented integration point for any future tooling that produces bus-shaped exports (e.g. a `bus-export` CLI command).
3. **Projects index (legacy).** `<workspace>/projects/`. List every project; for each, list lane folders `1-preparation` ... `7-archive`.
4. **Job state per project (legacy).** Each `<project>/<lane>/<job>/job.json` (id, title, state, agent, cliType, model, createdAt). Optionally `prompt.md`, `status.md`. Use these to confirm what the bus says.
5. **Activity logs per active job (legacy).** `<project>/<lane>/<job>/logs/cli-output.log`. The bus references slices via `artifact:log-slice`; tail directly when a finding needs the surrounding 50 lines of agent stdout.
6. **Supervisor logs (when bus migration incomplete).** `<workspace>/logs/meta/<project>/observations.jsonl`, `interventions.jsonl`, `reasoning.md`, `heartbeat.json`. Phase A (bus bridge writers) duplicates these into the bus; Phase C drops the duplicate. Read the legacy files only when the corresponding bus messages are missing.
7. **Recent commits in the stable repo.** `git -C <stable-checkout> log --since="$LOOKBACK"` (default lookback 8 hours). Confirms the orchestrator actually shipped work, not just churn.
8. **Backend crash marker.** `<stable-checkout>/logs/backend/last-crash.json`. The backend's process-wide exception handlers write this when an `UnobservedTaskException` or `UnhandledException` fires. When present, it carries `capturedAt`, `source`, `exceptionType`, `message`, and `topFrame`. If the file is fresh (within the lookback window) the review must surface it under "Anomalies" with the captured fields and pointer the operator at the matching daily log file `<stable-checkout>/logs/backend/<date>.log` for the full stack. The same data is served by `GET http://127.0.0.1:5030/api/diagnostics/last-crash` when the backend is up. The bus also carries a `kind:error` mirror from `runtime:taskboard` whenever the in-process writer is alive, so a healthy review cross-references both.
9. **Previous system review files.** `<workspace>/logs/system-review/`. Last review's open items are followed up in this review.

If a path does not exist, note it in the review and continue. Never fail the run on a missing path.

## Health checks every review must run

Run the eight bus-driven checks below before the open prose verdict. Each check produces zero or more findings; each finding carries a severity (`Info` / `Warn` / `High`), a one-line title, an evidence list of typed references back into the bus and on-disk artifacts, and a one-line recommendation. The reference helper script under `scripts/supervisor/system-health-check.mjs` implements all eight; treat its output as the structured backbone of the report and add prose context on top.

| # | Check | Trigger |
|---|---|---|
| 1 | **Long silent periods** | Per-project gap between consecutive bus messages > `ATP_SILENT_MINUTES` (default 60). Severity bumps to `High` when an open `lifecycle:RunStarted` had no matching `RunFinished` across the gap. |
| 2 | **Repeated interventions** | Per-project count of `kind:intervention` messages >= `ATP_INTERVENTION_THRESHOLD` (default 3). |
| 3 | **Repeated failed or cancelled runs** | Per-job count of `lifecycle:RunFinished` with `payload.outcome in {Failed, Cancelled}` >= `ATP_FAILED_RUN_THRESHOLD` (default 2). |
| 4 | **Token spikes** | Any `kind:token-usage` message with `tokens.output >= ATP_TOKEN_SPIKE_OUTPUT` (default 20000) or `tokens.dollars >= ATP_TOKEN_SPIKE_DOLLARS` (default 5). |
| 5 | **Many supporting jobs without accepted review** | Per-job count of `kind:decision` messages with `participantId` starting `support:` >= `ATP_SUPPORT_WITHOUT_REVIEW_THRESHOLD` (default 3) and no `lifecycle:JobStateMoved` to `6-completed` for the job. |
| 6 | **Stuck loops** | Per-job consecutive `kind:decision` messages from `orchestrator:*` with `topic in {reissue, heuristic-fallback}` >= `ATP_STUCK_LOOP_THRESHOLD` (default 3). |
| 7 | **Jobs that reached review with weak or missing evidence** | `lifecycle:JobStateMoved` to `4-auto-review` whose preceding run carries zero `artifacts[]` references (no screenshots, no log slices, no markdown reports). |
| 8 | **Backend crash markers** | `kind:error` from any `runtime:*` participant in the bus, plus a fresh `<stable>/logs/backend/last-crash.json` on disk. |

The thresholds are knobs for the operator, not policy. Document any deviation from defaults in the review's verdict paragraph so the next review can compare apples-to-apples.

## Dry-run mode and bus integration point

Even before every producer is wired into the bus, the skill is exercisable end-to-end via `scripts/supervisor/system-health-check.mjs`. The script reads bus-shaped JSONL from one of two sources:

- `--fixture <file>` - one JSONL file containing one `AgentMessage` per line. Used by the bundled sample fixture at `scripts/supervisor/fixtures/sample-bus.jsonl` and by any post-incident or test export.
- `--workspace <dir>` - a real workspace root; the script walks `logs/bus/<scope>/<date>.jsonl` itself.

`--out PATH` writes the Markdown report to disk; `--json` swaps to a machine-readable JSON document with the same fields. `--stable <dir>` lets the script also look at `<stable>/logs/backend/last-crash.json` for check 8.

The script is intentionally dependency-free Node: it can run from a fresh clone of stable without `npm install`, which matters when the failure mode under investigation is "stable's frontend is broken". The CLI session driven by `run-system-review.sh` may invoke it directly, embed its findings into the human-prose review, or skip it entirely and produce the review by hand. The expected usage is the first.

## Questions every review must answer

For the lookback window (default last 8 hours), in addition to the structured findings above:

1. **Throughput.** How many jobs moved into `4-auto-review`, `5-human-review`, `6-completed`? How many failed and stayed in `3-progress` or got dead-lettered to `3a-failed-pickup`? Compare against the previous review to spot regressions. Source: `lifecycle:JobStateMoved` messages or, when missing, `<workspace>/logs/pickup-failures.jsonl`.
2. **Stuck work.** Any task in `3-progress` for longer than its previous run took, or longer than a configurable threshold (default 30 min)? List with names and last-progress timestamps. Source: most recent `RunStarted` per active job, or the bus silent-period finding.
3. **Failure patterns.** Same task retried more than once? Same error class across multiple tasks? Same CLI repeatedly failing on the same project? Source: bus `repeated-failed-runs` findings + per-CLI `participantId` filter.
4. **Quota burn.** Per-CLI quota usage where logs expose it. Project the next reset; flag if a CLI is likely to exhaust before reset. Source: bus `token-usage` rollups and per-CLI quota logs.
5. **Supervisor health.** For each project with supervisor presence: heartbeat fresh (last `kind:heartbeat` from `supervisor:<project>` < 60 s)? Recent advisories (count by severity)? Any interventions fired? Were the interventions effective (did the situation improve or recur)?
6. **Findings ignored.** Advisories with severity Warn or High that have no follow-up task or acknowledgement after N hours. These are the highest-leverage items for the next review. Source: bus `kind:advisory` messages without a downstream `lifecycle:JobStateMoved` or follow-up `kind:decision`.
7. **Commit shape.** Stable's commit log over the lookback: are commits landing? Any push without a prior task? Any task without a corresponding commit?
8. **Anomalies.** Anything that deviates from the project's pattern: unusually long run, unusually short success, unexpected agent type swap, stop/restart of the backend.
9. **Verdict.** Plain prose: is the system behaving correctly? One paragraph. The user reads this first.

## Output

Write to `<workspace>/logs/system-review/<YYYY-MM-DD-HHmm>.md`:

```markdown
# System Review - <date> <time>

**Verdict.** One paragraph. Healthy / Caution / Action needed.

## Health findings (bus-driven)
<paste or summarize the eight-check structured findings here, severity-sorted; each
finding line ends with at least one `msg=<id>` reference back into the bus.>

## Throughput
- Jobs moved to 4-auto-review: N
- Jobs moved to 5-human-review: N
- Jobs moved to 6-completed: N
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

Keep the report tight: bullets over prose. The verdict paragraph and the health-findings section are the only two things a busy operator must read; everything else is supporting evidence.

Drill-down rule: every line in the health-findings section must carry one of `msg=<id>`, `job=<id>`, `run=<id>`, or `artifacts=<uri>`. A finding without a typed reference is a finding the operator cannot trust, so it does not ship.

## Cadence

- Default: every 4-8 hours, run manually by the user.
- Scheduling support is a later phase; keep this skill stand-alone for now.
- Each review references the previous review's open items so the carry-forward chain is visible.

## Failure modes

- **Stable checkout missing.** The script defaults to `C:\Projects\agent-taskboard-devspace\agent-taskboard-stable`; if that path does not exist, write a review file that says so and stop. Do not guess.
- **Workspace empty.** If `<workspace>/projects/` has no entries and `<workspace>/logs/bus/` is empty, write a review noting "no projects watched yet" and stop.
- **Bus directory missing.** If `<workspace>/logs/bus/` does not exist (Phase A bridge not yet enabled, or workspace unconfigured), fall back to the legacy raw streams in section 6 of "Inputs" and tag the review's verdict paragraph with `bus:absent` so the operator can tell the report is running on the older inputs.
- **Concurrent run.** If a review with the same minute timestamp already exists, append a `-2` suffix; do not overwrite.

## Reporting back to the orchestrator

This skill is independent of the supervisor and of the orchestrator. It does not write into the orchestrator's chat log and does not append to the bus. Its only output is the Markdown review file (and an optional sidecar JSON).

Two surfaces are planned for visibility, neither of which is part of this skill's write surface:

- **Project Screen Observability panel** (frontend). Already reads the bus through `/api/bus/{project}/...`. The system-review report is referenced by file path; future iteration may surface the latest review path as a banner on the home screen.
- **Per-job evidence.** When a review finding is scoped to one job (e.g. `weak-review-evidence` against `agent-taskboard::job-charlie`), the operator may copy the relevant slice into the job folder's `results/` directory as supporting evidence for the review decision. The system-review monitor never writes there itself.
