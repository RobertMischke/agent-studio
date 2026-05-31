# Troubleshooting

FAQ-style: known failure modes you may hit while operating agent-orchestrator, what they look like, and what to do. New entries belong here when the symptom is recurring and the root cause is non-obvious from the UI.

For deeper context, the structural references are:

- [../filesystem-contract.md](../filesystem-contract.md) - lane catalog, on-disk shape.
- [../../.agents/skills/job-api/references/known-pitfalls.md](../../.agents/skills/job-api/references/known-pitfalls.md) - operator-side pitfalls when scripting via the API.
- [../cli-skills/](../cli-skills/) - per-CLI quirks and known incidents.

## "The agent only emits sandbox errors"

Symptom: every shell command the agent tries to run fails with `windows sandbox: runner error: CreateProcessAsUserW failed: 1312`. The Activity Log fills with the same error and the run produces no edits.

Cause: Codex's Windows sandbox is set to `elevated`, which refuses every child-process spawn.

Fix: open `~/.codex/config.toml` and set:

```toml
[windows]
sandbox = "workspace-write"
```

Restart the Codex CLI (no backend restart needed - the next pickup re-spawns Codex with the new config). The full background and the runner's preventive complement live in [onboard-an-agent-cli.md](onboard-an-agent-cli.md) under "Codex on Windows: the sandbox quirk".

## "Auto-mode flipped to Manual after a short time"

Symptom: the project's runner-mode pill flips from `auto-continuous` to `manual` after a small number of failures, no further `2-ready` jobs are picked up.

Cause: the cross-slug infra circuit breaker ([../../backend/Services/Runner/CrossSlugInfraCircuitBreaker.cs](../../backend/Services/Runner/CrossSlugInfraCircuitBreaker.cs)) tripped on consecutive infra-class failures (sandbox errors, CLI crashes, missing-sentinel runs). The runner falls back to manual to keep a misconfigured CLI from burning through the queue.

What to check:

1. Look at the most recent jobs in `3-progress`, plus any the pickup loop just returned to `2-ready` (spawn failure) or escalated to `5-human-review` (task-shaped failure). Same failure category across them?
2. The bus event stream (`logs/bus/<project>/<date>.jsonl`) carries the circuit-breaker transitions and the reason it tripped.
3. Fix the underlying cause (sandbox config, CLI install, network), then resume manually with the runner-mode pill.

## "Counters in the header look wrong"

Symptom: the per-project counters in the header strip show numbers that don't match the lanes you see on the board, or show counts from a different project.

Cause: known cross-project leak in the header counter aggregation. Tracked as `bug-board-shows-wrong-project-counter-cross-project-leak` (currently in `5-human-review`).

Workaround: refresh the page; the counts recompute from `/api/jobs/grouped` on load. If the leak persists across refreshes, capture the response payload and attach it to the existing job rather than opening a new bug.

## "Two jobs sitting in 3-progress at the same time"

Symptom: the lane that is supposed to hold one job at a time briefly (or persistently) shows two cards.

Cause: the auto-review reissue race documented in [../../.agents/skills/job-api/references/known-pitfalls.md](../../.agents/skills/job-api/references/known-pitfalls.md) §5. When the runner moved a job to `4-auto-review` and the orchestrator decided "reissue" while a fresh job was being picked up, both ended up in `3-progress`.

Status: fixed by `fix-auto-review-reissue-must-go-to-ready-not-progress` (2026-05-11). Reissues now land in `2-ready order=0` instead of `3-progress`. If you still see two cards in `3-progress` after that date, inspect each job's `cli-output.log` for `Decision: reissue` to confirm whether the fix regressed or you are hitting a different race.

## "Aspect-runner says Concerns without a reason"

Symptom: a job's auto-review aspects all read `Concerns: Aspect runner produced no parseable verdict` with no body.

Cause: pre-2026-05-11 bug. The aspect-runner CLI invocation used `-p <multi-KB prompt>` as argv on Windows, which silently failed (argv-length overflow). All four aspects defaulted to the same template.

Status: fixed by routing the aspect-runner through `ICliOneShot` (stdin-piped). Aspects produced after 2026-05-11 should carry real verdicts. When triaging the lingering 100+ jobs in `5-human-review` from that day, filter aspect rows whose summary matches `/Aspect runner produced no parseable verdict/i` and treat them as no-signal.

## "PUT /api/runner/<project>/mode returns 400"

Symptom: enabling `auto-continuous` for a newly-added project returns `400 Invalid project or mode`.

Cause: `TaskRunnerService` only creates per-project runners at startup. Hot-reload of `WatchPaths` makes the project visible to `/api/watch-paths` but doesn't register the runner.

Fix: restart the backend (`./api.sh restart`). Tracked for a durable fix as `fix-runner-mode-rejects-newly-added-projects`. Full context: [onboard-a-project.md](onboard-a-project.md) Step 2.

## "Codex run lands as missing-terminal-sentinel"

Symptom: a Codex job finishes the work cleanly but the run is marked `missing-terminal-sentinel` and lands in auto-review instead of `4-auto-review -> 5-human-review` with a clean Done.

Cause: Codex has no `--append-system-prompt` flag, so the sentinel grammar is only injected via `CodexCliService.BuildSystemPromptPrefix`. On a resume turn the fresh-start template is not re-rendered. Without the prefix, Codex regularly drops the terminal sentinel.

Fix: the runner already prepends the prefix on every invocation. If you see this on runs that came from the runner (not a manual `codex exec`), check that the prefix length-guard test (`CodexCliServiceTests.BuildSystemPromptPrefix_StaysShort`) didn't get accidentally stripped. The grammar lives in [../agent-task-contract.md](../agent-task-contract.md).

## "Empty shell folder in a lane"

Symptom: a lane folder exists with only a `logs/` subdirectory; no `job.json`, no `prompt.md`. The API refuses to move or delete it because there is no `job.json` to identify the job.

Cause: orchestrator crash mid-transition or a multi-lane race.

Fix: this is an exception to the "API only" rule for job-folder mutations. Delete the empty shell folder directly:

```js
fs.rmSync(folderPath, { recursive: true, force: true });
```

See [../../.agents/skills/job-api/references/known-pitfalls.md](../../.agents/skills/job-api/references/known-pitfalls.md) §7.

## "Crash recovery committed my uncommitted edits"

Symptom: you ran `./api.sh start` while you had uncommitted edits in the dev checkout; on boot a `chore(crash-recovery)` commit appeared with your work folded in.

Cause: the crash-recovery service auto-commits uncommitted edits on boot to keep the runner's commit boundary clean. This is the documented behaviour but it's surprising the first time.

Fix going forward: commit before booting the dev backend. The orchestrator memory entry "Crash recovery auto-commits before you can" captures the reminder.

## "I want to script a job move but the API rejects it"

Symptom: `POST /api/jobs/<id>/move` returns `409 Job already exists or invalid input` even though the slug is unique.

Cause: you passed `rootPath` as `watchPath`. The server resolves jobs against the *resolved job-folder root* under the workspace, not the project's source tree.

Fix: use the `path` field returned by `GET /api/watch-paths`, not `rootPath`. The full pitfall and ready-to-use Node templates live in [../../.agents/skills/job-api/SKILL.md](../../.agents/skills/job-api/SKILL.md).
