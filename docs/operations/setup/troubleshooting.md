# Troubleshooting

FAQ-style: known failure modes you may hit while operating agent-orchestrator, what they look like, and what to do. New entries belong here when the symptom is recurring and the root cause is non-obvious from the UI.

For deeper context, the structural references are:

- [../filesystem-contract.md](../../system/contracts/filesystem.md) - lane catalog, on-disk shape.
- [../../.agents/skills/task-api/references/known-pitfalls.md](../../../.agents/skills/task-api/references/known-pitfalls.md) - operator-side pitfalls when scripting via the API.
- [../cli-skills/](../../system/cli/skills) - per-CLI quirks and known incidents.

## "The agent only emits sandbox errors"

Symptom: every shell command the agent tries to run fails with `windows sandbox: runner error: CreateProcessAsUserW failed: 1312`. The Activity Log fills with the same error and the run produces no edits.

Cause: Codex's Windows sandbox is set to `elevated`, which refuses every child-process spawn.

Fix: open `~/.codex/config.toml` and set:

```toml
[windows]
sandbox = "workspace-write"
```

Restart the Codex CLI (no backend restart needed - the next pickup re-spawns Codex with the new config). The full background and the runner's preventive complement live in [onboard-an-agent-cli.md](./onboard-an-agent-cli.md) under "Codex on Windows: the sandbox quirk".

## "Auto-mode flipped to Manual after a short time"

Symptom: the project's runner-mode pill flips from `auto-continuous` to `manual` after a small number of failures, no further `2-ready` jobs are picked up.

Cause: the cross-slug infra circuit breaker ([../../backend/Services/Runner/CrossSlugInfraCircuitBreaker.cs](../../../backend/Services/Runner/CrossSlugInfraCircuitBreaker.cs)) tripped on consecutive infra-class failures (sandbox errors, CLI crashes, missing-sentinel runs). The runner falls back to manual to keep a misconfigured CLI from burning through the queue.

What to check:

1. Look at the most recent jobs in `3-progress`, plus any the pickup loop just returned to `2-ready` (spawn failure) or escalated to `5-human-review` (task-shaped failure). Same failure category across them?
2. The bus event stream (`logs/bus/<project>/<date>.jsonl`) carries the circuit-breaker transitions and the reason it tripped.
3. Fix the underlying cause (sandbox config, CLI install, network), then resume manually with the runner-mode pill.

## "Counters in the header look wrong"

Symptom: the per-project counters in the header strip show numbers that don't match the lanes you see on the board, or show counts from a different project.

Cause: known cross-project leak in the header counter aggregation. Tracked as `bug-board-shows-wrong-project-counter-cross-project-leak` (currently in `5-human-review`).

Workaround: refresh the page; the counts recompute from `/api/tasks/grouped` on load. If the leak persists across refreshes, capture the response payload and attach it to the existing job rather than opening a new bug.

## "Two jobs sitting in 3-progress at the same time"

Symptom: the lane that is supposed to hold one job at a time briefly (or persistently) shows two cards.

Cause: the auto-review reissue race documented in [../../.agents/skills/task-api/references/known-pitfalls.md](../../../.agents/skills/task-api/references/known-pitfalls.md) §5. When the runner moved a job to `4-auto-review` and the orchestrator decided "reissue" while a fresh job was being picked up, both ended up in `3-progress`.

Status: fixed by `fix-auto-review-reissue-must-go-to-ready-not-progress` (2026-05-11). Reissues now land in `2-ready order=0` instead of `3-progress`. If you still see two cards in `3-progress` after that date, inspect each job's `cli-output.log` for `Decision: reissue` to confirm whether the fix regressed or you are hitting a different race.

## "Aspect-runner says Concerns without a reason"

Symptom: a job's auto-review aspects all read `Concerns: Aspect runner produced no parseable verdict` with no body.

Cause: pre-2026-05-11 bug. The aspect-runner CLI invocation used `-p <multi-KB prompt>` as argv on Windows, which silently failed (argv-length overflow). All four aspects defaulted to the same template.

Status: fixed by routing the aspect-runner through `ICliOneShot` (stdin-piped). Aspects produced after 2026-05-11 should carry real verdicts. When triaging the lingering 100+ jobs in `5-human-review` from that day, filter aspect rows whose summary matches `/Aspect runner produced no parseable verdict/i` and treat them as no-signal.

## "PUT /api/runner/<project>/mode returns 400"

Symptom: enabling `auto-continuous` for a newly-added project returns `400 Invalid project or mode`.

Cause: `TaskRunnerService` only creates per-project runners at startup. Hot-reload of `WatchPaths` makes the project visible to `/api/watch-paths` but doesn't register the runner.

Fix: restart the backend (`./api.sh restart`). Tracked for a durable fix as `fix-runner-mode-rejects-newly-added-projects`. Full context: [onboard-a-project.md](./onboard-a-project.md) Step 2.

## "Codex run lands as missing-terminal-sentinel"

Symptom: a Codex job finishes the work cleanly but the run is marked `missing-terminal-sentinel` and lands in auto-review instead of `4-auto-review -> 5-human-review` with a clean Done.

Cause: Codex has no `--append-system-prompt` flag, so the sentinel grammar is only injected via `CodexCliService.BuildSystemPromptPrefix`. On a resume turn the fresh-start template is not re-rendered. Without the prefix, Codex regularly drops the terminal sentinel.

Fix: the runner already prepends the prefix on every invocation. If you see this on runs that came from the runner (not a manual `codex exec`), check that the prefix length-guard test (`CodexCliServiceTests.BuildSystemPromptPrefix_StaysShort`) didn't get accidentally stripped. The grammar lives in [../agent-task-contract.md](../../system/contracts/agent-task.md).

## "Empty shell folder in a lane"

Symptom: a lane folder exists with only a `logs/` subdirectory; no `job.json`, no `prompt.md`. The API refuses to move or delete it because there is no `job.json` to identify the job.

Cause: orchestrator crash mid-transition or a multi-lane race.

Fix: use a dedicated recovery/delete API when one exists. If the current API
cannot see the shell folder, stop and ask for an explicit operator decision
before any filesystem cleanup; do not hide direct deletion inside an automated
triage script.

See [../../.agents/skills/task-api/references/known-pitfalls.md](../../../.agents/skills/task-api/references/known-pitfalls.md) §7.

## "Crash recovery committed my uncommitted edits"

Symptom: you ran `./api.sh start` while you had uncommitted edits in the dev checkout; on boot a `chore(crash-recovery)` commit appeared with your work folded in.

Cause: the crash-recovery service auto-commits uncommitted edits on boot to keep the runner's commit boundary clean. This is the documented behaviour but it's surprising the first time.

Fix going forward: commit before booting the dev backend. The orchestrator memory entry "Crash recovery auto-commits before you can" captures the reminder.

## "I want to script a job move but the API rejects it"

Symptom: `POST /api/tasks/<id>/move` returns `409 Job already exists or invalid input` even though the slug is unique.

Cause: you passed `rootPath` as `watchPath`. The server resolves jobs against the *resolved job-folder root* under the workspace, not the project's source tree.

Fix: use the `path` field returned by `GET /api/watch-paths`, not `rootPath`. The full pitfall and ready-to-use Node templates live in [../../.agents/skills/task-api/SKILL.md](../../../.agents/skills/task-api/SKILL.md).

## "Claude quota panel is empty / plan shows unknown"

Symptom: the Claude quota widget (status bar donut, admin panel) shows no usage windows and no plan, even though `claude` works fine when you run it yourself.

Cause: the backend's quota probe drives `claude` headlessly and sends `/usage`. If the CLI has never been taken past its first-run onboarding (folder-trust dialog, theme picker, a "try the new renderer" upsell), those screens sit in front of the ready REPL and swallow the `/usage` command, so the probe returns zero windows. `ClaudeQuotaProbe.LooksLikeOnboardingWizard` (see [ClaudeQuotaProbe.cs](../../../backend/Features/Cli/Quota/ClaudeQuotaProbe.cs)) detects this case and logs it distinctly from an actual `/usage` output-format drift.

Fix: run `claude` interactively yourself once, log in, and click through every first-run screen until you reach the normal `? for shortcuts` prompt. The onboarding state persists in `~/.claude.json`, so this is a one-time fix per machine/user. See [getting-started.md](./getting-started.md) step 1 for the full first-install version of this.

## "claude vanished from PATH, the npm package is still there" (AGT-2673)

Symptom: on a Windows host, `claude` (and its `.cmd`/`.ps1` siblings) stop resolving on PATH with no orphan `.claude-<random>` files and no stub binary left behind - `node_modules/@anthropic-ai/claude-code` is present and looks intact, but npm's own global bin-shim linking is simply gone. Logged twice against the Windows control-plane host (`docs/operations/live-improvement-log/index.html`, 2026-08-13 and 2026-08-18); the installed version had moved between sightings (2.1.231 -> 2.1.234), which points at a racing auto-updater rather than a one-off interrupted install.

Cause: `NpmShimHealer`'s steps 1-4 ([NpmShimHealer.cs](../../../backend/Features/Cli/Execution/NpmShimHealer.cs)) repair every *anthropic-postinstall* failure shape (orphan atomic-rename files, a stub binary swap, staging orphans) but none of them touch npm's own bin-shim linking step. When the shim vanishes with nothing left behind to rename back, steps 1-4 have nothing to do.

Fix (already automatic): step 5 in `NpmShimHealer.TryHealClaudeAsync` now falls back to `npm install -g @anthropic-ai/claude-code` when the `claude.cmd` shim is still missing after steps 1-4 *and* the package directory is present (a package directory that is absent entirely means there is nothing to repair, so the fallback is skipped and the CLI is never auto-reinstalled - see `NpmShimHealer.ShouldAttemptNpmInstallFallback`). The whole repair pass - including this fallback - is bounded to one attempt per hour by `CliRepairGate` ([CliRepairGate.cs](../../../backend/Features/Cli/Execution/CliRepairGate.cs)), so a fast-repeating pickup loop against a persistently broken install cannot turn into an install storm. That bound lives in process-static state, so it is one attempt per backend process per hour: a backend restart resets the window, and two backends on the same host would each get their own attempt. Every attempt (success or failure) is journaled to `<workspace>/logs/cli-repairs.jsonl` by `CliRepairLog.cs` with the `package.json` version read immediately before and after the pass. Note what that pair does and does not prove: both reads happen at repair time, so they show whether the *repair* changed the installed version, not what the version was before the breakage. The auto-update evidence is the journal accumulating across incidents - comparing `versionBefore` between consecutive rows is what surfaces a 2.1.231 -> 2.1.234 style drift. A **failed** repair also surfaces in the executive summary's crash list (`WorkspaceSummaryService.ReadFailedCliRepairs`) as `cli-repair-failed` (package present, repair attempted, still broken) or `cli-not-installed` (no package at all - not a repair failure). A **successful** repair is intentionally not alarmed on; it is still visible as an `Information`-level `CliRepairGate: claude repaired at <UTC time> (<before> -> <after>)` log line and as a journal row, per the "alarm only if repair fails" design.

What to check if `claude` still will not run after an hour: read the newest row of `logs/cli-repairs.jsonl` for the real diagnostic (`error`, `versionBefore`/`versionAfter`, `actions`), then fall back to a manual `npm install -g @anthropic-ai/claude-code` and file/upgrade the recurring-defect entry in the live improvement log if the same host trips this a third time.

Scope note: this fix lives in `NpmShimHealer.cs`, wired only into the legacy rollback (`BuiltInCliBehaviors.cs`) and the non-agent Claude one-shot (`ClaudeOneShot.cs`) - the paths `LegacyNpmShimRepairContractTests` pins as the only two callers. CAR-backed runs (the execution engine `docs/operations/car-migration-plan.md` is migrating toward) carry their own npm-shim healer inside the CAR package and are intentionally not duplicated here (see that plan's "what CAR brings and is not rebuilt" table). If a CAR-backed run hits this same symptom, that is a CAR-package gap (PROJ-011), not a Studio-repo one - check which engine ran the failing job before assuming this fix applies.



**Manual break-and-heal rehearsal** (for verifying this on the actual Windows control-plane host - the automated tests below only exercise the OS-independent seams, since `NpmShimHealer` short-circuits on non-Windows hosts):

1. Note the current version: `type %APPDATA%\npm\node_modules\@anthropic-ai\claude-code\package.json` (or open it) and record the `version` field.
2. Break the shim without leaving repairable debris: close every running `claude`/backend process, then delete just the top-level shims: `del %APPDATA%\npm\claude %APPDATA%\npm\claude.cmd %APPDATA%\npm\claude.ps1`. Confirm `node_modules\@anthropic-ai\claude-code\bin\claude.exe` is still present and larger than 4 KB (that is what makes this the "shim missing, package present" shape rather than the stub/orphan shapes steps 1-4 already cover).
3. Trigger the heal path: run `claude --version` directly to confirm it now fails, then let a real pickup or one-shot call fire (or call `BuiltInCliBehaviors.ClaudeEnsureCliHealthyAsync` / `ClaudeOneShot.RunAsync` in-process, whichever is convenient).
4. Confirm the shim is restored: `claude --version` succeeds and `%APPDATA%\npm\claude.cmd` exists again.
5. Confirm the evidence trail: tail `<workspace>/logs/cli-repairs.jsonl` for a row with `"available":true`, `"packagePresent":true`, and `versionBefore`/`versionAfter` matching what you recorded in step 1 (equal if no auto-update raced the rehearsal, different if one did); confirm the backend log shows the `CliRepairGate: claude repaired at ...` line.
6. Confirm the cooldown: repeat step 2 immediately and re-trigger the heal path within the same hour. Expect `claude` to stay broken and the journal to gain no new row (the attempt was suppressed by `CliRepairGate`'s cooldown) until an hour has passed since step 3.
