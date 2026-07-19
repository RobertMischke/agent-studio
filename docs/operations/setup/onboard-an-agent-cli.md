# Onboarding a new CLI agent

agent-orchestrator drives four coding-agent CLIs: Claude Code, Codex, GitHub Copilot, and Gemini. Each is a separate install with its own auth, config file, and quirks. This page is the checklist to get one of them running cleanly on a new machine (or to fix one that has started misbehaving).

For deep operational references (frame model, session capture, watchdog tuning, fixtures) read the matching per-CLI skill in [../cli-skills/](../../system/cli/skills) - this page links to them.

## Common contract

Every CLI must satisfy the cross-CLI invariants in [../supported-clis.md](../../system/cli/supported-clis.md): headless invocation, session resume, model selection, quota probing, plain stdout/stderr. The runner picks one of them per job via `cliType` and dispatches through [`CliRouter`](../../../backend/Services/Cli/CliRouter.cs).

### Sentinel awareness applies to every CLI

The orchestrator decides outcomes by parsing terminal sentinels: `[[TASK_DONE]]`, `[[TASK_BLOCKED:<reason>]]`, `[[TASK_NEEDS_INPUT:<reason>]]`, `[[TASK_NOOP]]`. The grammar lives in [../agent-task-contract.md](../../system/contracts/agent-task.md) and is enforced by `AgentOutcomeAnalyzer` (see [../../backend/Services/Runner/AgentOutcomeAnalyzer.cs](../../../backend/Services/Runner/AgentOutcomeAnalyzer.cs)).

The default runtime prompt that wraps every task already tells the agent to emit one. **Codex needs an extra nudge** because it has no `--append-system-prompt` flag (see "Codex" below).

## Claude Code

| | |
|---|---|
| Install | `npm install -g @anthropic-ai/claude-code` |
| Expected binary | `claude` (resolves to `node_modules\@anthropic-ai\claude-code\bin\claude.exe` via `PATHEXT` on Windows) |
| Config override | `ClaudeCli:Path` in `backend/appsettings.Local.json` |
| Auth | `claude` interactive login the first time; credentials persist in `~/.claude/`. |
| Deep ref | [../cli-skills/cli-claude.md](../../system/cli/skills/cli-claude.md) |

**Recommended defaults.** Leave the CLI defaults alone - the runner passes the flags it needs (`-p`, `--output-format stream-json`, `--verbose`, `--dangerously-skip-permissions`). The system-prompt overlay file ([`agent-rules/core.md`](../../../agent-rules/core.md)) is injected via `--append-system-prompt-file`.

**Known quirks**:

- **argv-length on Windows.** A multi-KB prompt passed as `-p <prompt>` on the Windows command line silently fails (empty CLI response). Production code paths use `ICliOneShot` ([../../backend/Services/Cli/OneShot/ICliOneShot.cs](../../../backend/Services/Cli/OneShot/ICliOneShot.cs)) or stdin-piped `Process.Start` to bypass this. The drift analyser in [`CodePatternDriftAnalysisService.cs`](../../../backend/Services/Drift/CodePatternDriftAnalysisService.cs) flags new `-p <multi-KB-string>` call sites as regressions.
- **Claude CLI repair on Windows after an interrupted update.** Dot-prefix shims and missing postinstall under `C:\Users\rmisc\AppData\Roaming\npm\`. The fix is to reinstall the npm package; see the orchestrator memory entry "Claude CLI repair on Windows".

## Codex

| | |
|---|---|
| Install | `npm install -g @openai/codex` |
| Expected binary | `codex` |
| Config override | `CodexCli:Path` |
| Local config | `~/.codex/config.toml` |
| Auth | `codex` interactive login the first time. |
| Deep ref | [../cli-skills/cli-codex.md](../../system/cli/skills/cli-codex.md) |

### Codex on Windows: the sandbox quirk (read this)

`~/.codex/config.toml` controls Codex's process sandbox. The `[windows]` section has a `sandbox` key that **must not be `elevated`**:

```toml
# ~/.codex/config.toml
[windows]
sandbox = "workspace-write"   # OK. This is what we ship with.
# sandbox = "elevated"        # NOT OK on Windows: blocks every shell command.
```

When `sandbox = "elevated"`, the Windows sandbox runner (`windows-sandbox-rs`) refuses every `CreateProcessAsUserW` call with OS error 1312 (`A specified logon session does not exist`). The user-visible symptoms are:

- Every shell command the agent tries to run fails with `windows sandbox: runner error: CreateProcessAsUserW failed: 1312`.
- The agent retries in a tight loop and produces no progress.
- Auto-mode flips back to manual after the circuit breaker fires.

The runner has two layers of defense:

1. **Reactive in-stream match.** `AgentEnvironmentDetector` ([../../backend/Services/Runner/AgentEnvironmentDetector.cs](../../../backend/Services/Runner/AgentEnvironmentDetector.cs)) classifies the line as `codex-windows-sandbox` and routes the run to human review with `[[TASK_BLOCKED:windows-sandbox]]`.
2. **Preventive prompt prefix.** `CodexCliService.BuildSystemPromptPrefix` ([../../backend/Services/Cli/CodexCliService.cs](../../../backend/Services/Cli/CodexCliService.cs)) prepends a hint to every Codex invocation on Windows telling the agent not to retry on this error and to surface `[[TASK_BLOCKED:windows-sandbox]]` instead.

Neither of these *fixes* a misconfigured sandbox. They turn a wedged loop into a clean blocker. Fix the config.

### Codex sentinel awareness

Codex has no `--append-system-prompt` flag, so the runner can't inject the sentinel grammar through CLI flags. Instead, `CodexCliService.BuildSystemPromptPrefix` prepends a short system-prompt prefix to the positional prompt argument on **every** invocation (fresh runs and resumes). The prefix carries the sentinel grammar so the agent reliably emits `[[TASK_DONE]]` (or one of the others) at end of turn.

When you bypass that codepath (one-off `codex exec --json "<your prompt>"` from the shell), the agent will often not emit a sentinel, the runner will mark the run as `missing-terminal-sentinel`, and the job will land in auto-review for inspection. This is expected; don't strip the prefix from the runner to fix it.

### Other Codex quirks

- **Trust prompt accepts a bare `Enter`.** Use `<Enter>` alone over a PTY; `1<Enter>` works but leaves a stray `1` in the input box. The `/status` probe relies on this.
- **`exec resume <uuid>` is positional** before `--json`. `--resume=<uuid>` is Copilot's flag; `-r <uuid>` is Claude/Gemini's. Codex is different.
- **Codex token-usage frames** don't reach the message bus today (tracked as `bug-codex-token-usage-not-on-bus`). Pricing displays are approximate.

## GitHub Copilot

| | |
|---|---|
| Install | `npm install -g @github/copilot-cli` |
| Expected binary | `copilot` |
| Config override | `CliPath` (legacy name in `appsettings.Local.json`) |
| Auth | `GITHUB_TOKEN` env var, or `gh auth token` on a logged-in `gh` install |
| Deep ref | [../cli-skills/cli-copilot.md](../../cli/skills/cli-copilot.md) |

**Recommended defaults.** None - Copilot has no JSON output mode worth toggling. The runner generates its own slug (`taskboard-<jobId>-YYYYMMDDHHmm`) and passes it as both `--name` (fresh) or `--resume` (continue).

**Known quirks**:

- **Legacy code path.** `CopilotCliService` predates `CliExecutionServiceBase` and reimplements lifecycle, persistence, reattach, and reaping. Don't refactor it onto the base class as a side quest.
- **No JSON frame model.** Output is plain text; the parser scrapes the footer for the `Remaining reqs.: ±NN.N%` quota line.
- **Auth surprises.** If `GITHUB_TOKEN` is unset *and* `gh` is not logged in, Copilot prompts interactively and the runner stalls. Make sure one of the two paths works before queuing a job.

## Gemini

| | |
|---|---|
| Install | `npm install -g @google/gemini-cli` |
| Expected binary | `gemini` (verified against v0.39.1) |
| Config override | `GeminiCli:Path` |
| Auth | `gemini` interactive auth; credentials in `~/.gemini/` |
| Deep ref | [../cli-skills/cli-gemini.md](../../system/cli/skills/cli-gemini.md) |

**Recommended defaults.** The runner already passes the headless flags it needs: `--skip-trust` (bypass folder-trust modal) and `-y` / `--yolo` (auto-approve tool calls).

**Known quirks**:

- **Stream-json frame shape differs from Claude's.** Same `--output-format stream-json` flag name, different frame catalog. The parser lives in [`GeminiCliService`](../../../backend/Services/Cli/GeminiCliService.cs).
- **Stdout-buffering bug.** Gemini buffers stdout under certain conditions; the Activity Log can appear frozen even when the model is producing. See [`cli-gemini.md`](../../system/cli/skills/cli-gemini.md) for the latest workaround.
- **Slug map.** Sessions are stored under `~/.gemini/tmp/<project-slug>/chats/...` with the slug map in `~/.gemini/projects.json`. If the slug map gets corrupted, sessions become unresumable; delete the entry and let Gemini regenerate it.

## After install: verify with a hello-world

Each CLI has a Playwright spec that drives one real task end-to-end (marked `@billable` because it consumes real quota). Run the one for the CLI you just installed:

```sh
# from the repo root
npx playwright test --grep "@billable" -g "claude-hello-world"
# or codex-hello-world / copilot-hello-world / gemini-hello-world
```

These are cheap (one Haiku-class call, ~10 s) and prove the install end-to-end. If the spec fails, the per-CLI deep ref ([../cli-skills/](../../system/cli/skills)) is the next stop.
