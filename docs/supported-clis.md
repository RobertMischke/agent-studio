# Supported CLIs

This document is the **contract** every CLI integration in the Agent Task Processor must satisfy. It describes:

1. What "supported" means.
2. The capabilities every supported CLI must provide.
3. The CLIs supported today and their known quirks.
4. The step-by-step procedure for adding a new CLI.

Whenever a CLI integration is added, changed, or audited, this file is updated **in the same PR**.

> **Language:** English. See [AGENTS.md](../AGENTS.md#documentation-language).

---

## 1. What "supported" means

A "supported CLI" is a coding-agent CLI (Claude Code, Codex, Copilot, Gemini, …) that the task processor can drive end-to-end:

- The task processor can spawn a process for it from a job folder.
- It surfaces live output in the Activity Log.
- The user can pick a model from the side-sheet.
- The user can see plan + remaining quota in the right-hand quota side-sheet.
- The user can cancel a running job.
- A new run can either start fresh or, where the CLI supports it, resume an existing session.

If any of these capabilities are missing, the CLI is **partially** supported and the gap must be documented in section 3.

---

## 2. Required capabilities

Each capability has: a contract, the code that implements it, and the test that proves it.

### 2.1 Process lifecycle

**Contract.** Given a prompt, working directory, optional session id, optional model, the CLI driver must spawn a child process, stream stdout/stderr to the API consumer line-by-line, accept cancellation, and report final exit code + duration.

**Code.** Subclass [`CliExecutionServiceBase`](../backend/Services/Cli/CliExecutionServiceBase.cs) and implement `BuildStartInfo`. The base class handles spawning, streaming, cancellation, persistence, and reattach.

**Test.** A backend xUnit test that spawns the CLI in a temp directory and asserts a non-empty output line + clean exit. An E2E `@billable` "hello world" Playwright spec that drives the UI.

### 2.2 Session model

**Contract.** The driver decides whether the CLI has a usable session concept. If it does, it must:

- Define `IsCompatibleSessionName(string?)` strictly — reject ids that came from a different CLI (Claude UUIDs vs. Copilot slugs etc.). Accepting an alien id leads to silent hangs.
- Build the resume command in `BuildStartInfo` when `resumeSession=true`.
- Optionally capture the session id from CLI output in `OnOutputLine` (Codex pattern).

If the CLI has no usable session concept, `IsCompatibleSessionName` returns `false` for everything and the UI Continue button stays disabled automatically.

**Session storage discovery.** [`SessionRegistry`](../backend/Services/Cli/SessionRegistry.cs) reads each CLI's on-disk session store to populate the Sessions side-sheet. New CLIs add their own `BuildXxxProjects()` method here. Disk reads are best-effort — missing files mean "no sessions", not an error.

### 2.3 Model selection

**Contract.** `GetModelCatalogAsync` returns a list of models the user can pick from. Acceptable sources, in preference order:

1. **Live discovery** — query the CLI for its current model list (Copilot's PTY probe is the reference pattern).
2. **Static list with version** — hardcoded list keyed off the CLI version when discovery isn't feasible (Claude pattern).
3. **Empty list** — the CLI auto-picks; the user has no choice. Fine for v1, document it.

The frontend's model dropdown reads `/api/cli/{cliType}/models`. No CLI-specific UI code is needed if the JSON shape (`CliModelCatalog`) is honoured.

**Selected model.** The chosen model id is passed into `BuildStartInfo` and added as the appropriate flag (`-m`, `--model`, …).

### 2.4 Quota probe

**Contract.** A `QuotaProbeBase` subclass returns a `QuotaSnapshot` with:

- `Plan` — human-readable subscription tier ("Pro", "Plus", "Free", …) or `null`.
- `Windows[]` — one or more `QuotaWindow`s with `UsedPct` (0–100, may exceed when overage allowed), `ResetAt` UTC, and `ResetLabel` for display.
- `Source` — what the probe queried (`/usage`, `/status`, footer text, HTTP endpoint, …).
- `RawSample` — truncated raw output for debugging.

**Implementation pattern.** Most probes spawn the CLI in a scratch dir under `%TEMP%/agent-taskboard-quota/<cliType>` via a PTY, send slash-commands, scrape the rendered panel. See `ProbeWithStepsAsync` in [`QuotaProbeBase`](../backend/Services/Quota/QuotaProbeBase.cs).

**Aggregation.** [`QuotaService`](../backend/Services/Quota/QuotaService.cs) aggregates all registered `IQuotaProbe`s and serves `/api/cli/quota`. New CLIs register via `services.AddSingleton<IQuotaProbe, XxxQuotaProbe>()` in `Program.cs`.

**Refresh cadence.** Background refresh is automatic; the user can force a refresh per-CLI via the side-sheet button.

### 2.5 Logging & Activity Log

**Contract.** Output the user sees in the Activity Log must be:

- **Streamed,** not buffered until the run finishes.
- **Marker-line formatted** so the frontend's `activity-log.parser` can classify entries (Read/Search/Edit/Run/Todo/Task/Messages).
- **Free of ANSI escapes** in the persisted form (the base class strips them on write).
- **UTF-8 safe** — the base class forces UTF-8 stdout/stderr encoding; do not override.

**Implementation.** Override `TransformReadLine(CliOutputLine raw)` to translate the CLI's native output into marker lines. Reference: [`ClaudeCliService.TransformReadLine`](../backend/Services/Cli/ClaudeCliService.cs) — it expands stream-json NDJSON into `● Read /path`, `● Edit /path`, `● Run <cmd>`, etc.

If the CLI emits plain text already in a parser-friendly shape, `TransformReadLine` can be the default identity transform.

### 2.6 Cancellation

**Contract.** A running job must be cancellable from the UI. The base class kills the process tree on cancel.

**CLI-specific cleanup.** If the CLI leaves orphaned PTY children, modal pickers, or background helpers, the driver is responsible for cleaning them up. Quota probes additionally send `<Esc><Esc>` before tearing down to close any open modal pickers — keep doing this for new CLIs.

### 2.7 Availability & authentication check

**Contract.** `TestCliPath()` returns `(Available, Version, ResolvedPath)`. Default implementation runs `<cli> --version` and parses the first non-empty line. Override only if the CLI's version surface differs.

**Authentication.** Most CLIs auth out-of-band (browser login, env var, gh-cli token). The task processor does **not** drive login flows — if `TestCliPath` succeeds but the CLI is logged out, the failure surfaces when the first quota probe or job starts. New CLIs should make the failure mode obvious in their error message.

---

## 3. Currently supported CLIs

### 3.1 Claude Code (`claude`)

| Aspect | Status |
|--------|--------|
| Process lifecycle | ✅ `claude -p "<prompt>" --output-format stream-json --verbose --dangerously-skip-permissions` |
| Session model | ✅ UUIDs only (`IsCompatibleSessionName` rejects slugs); resume via `-r <uuid>`; named-session create via `--name` |
| Model selection | ⚠ Hardcoded list (Opus 4.7, Sonnet 4.6, Haiku 4.5) — no live discovery yet |
| Quota probe | ✅ `/usage` PTY probe — session bucket + weekly bucket |
| Logging | ✅ stream-json → marker lines via `TransformReadLine` |
| Cancellation | ✅ |
| Availability | ✅ `claude --version` |
| Session storage | `~/.claude/projects/<encoded-cwd>/<uuid>.jsonl` |

**Quirks.**
- Claude reads stdin even with `-p`; the base class closes stdin immediately to avoid a 3 s "no stdin received" warning.
- The npm shim on Windows points to `node_modules/@anthropic-ai/claude-code/bin/claude.exe`. An interrupted update can leave a `claude.exe.old.<timestamp>` and no `claude.exe`, breaking `--version`. Reinstall via `npm i -g @anthropic-ai/claude-code` to fix.

### 3.2 Codex CLI (`codex`)

| Aspect | Status |
|--------|--------|
| Process lifecycle | ✅ `codex exec [resume <uuid>] --json [-m <model>] "<prompt>"` |
| Session model | ✅ UUIDs only; first run auto-creates id, captured from the first `session_meta` JSON line |
| Model selection | ✅ Live discovery via `CodexModelDiscovery` |
| Quota probe | ✅ `/status` PTY probe — 5h + weekly buckets (Codex reports % left, we invert to % used) |
| Logging | ⚠ Pass-through; no marker-line transform yet |
| Cancellation | ✅ |
| Availability | ✅ `codex --version` |
| Session storage | `~/.codex/session_index.jsonl` + `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl` |

**Quirks.**
- Codex's trust prompt has "1. Yes, continue" pre-selected and accepts a bare Enter. Sending `1<Enter>` works but leaves a stray `1` in the input box that prefixes the next slash command — use `<Enter>` alone.
- Trust + welcome + `/status` is a fragile multi-step probe; see comments in [`CodexQuotaProbe`](../backend/Services/Quota/CodexQuotaProbe.cs).

### 3.3 GitHub Copilot CLI (`copilot`)

| Aspect | Status |
|--------|--------|
| Process lifecycle | ✅ Custom code path in [`CopilotCliService`](../backend/Services/CopilotCliService.cs) (predates `CliExecutionServiceBase`) |
| Session model | ✅ Named slugs persisted in the job record |
| Model selection | ✅ Live discovery via `CopilotModelDiscovery` |
| Quota probe | ✅ Footer scrape — `Remaining reqs.: ±NN.N%`; absolute counts derived from the configured plan |
| Logging | ✅ Pass-through; CLI already emits clean text |
| Cancellation | ✅ |
| Availability | ✅ `copilot --version`; additionally `HasGitHubToken()` checks the env / config |
| Session storage | `~/.copilot/history/<name>.jsonl` (when present) |

**Quirks.**
- Copilot is the legacy code path — refactoring it onto `CliExecutionServiceBase` is out of scope.
- Plan must be set in config (`Quota:CopilotPlan`) because the CLI doesn't echo it.
- Premium-request counter resets on the 1st of the month, 00:00 UTC.

### 3.4 Gemini CLI (`gemini`)

Verified against `@google/gemini-cli` v0.39.1.

| Aspect | Status |
|--------|--------|
| Process lifecycle | ✅ `gemini -p "<prompt>" -o stream-json --skip-trust -y [-m <id>] [-r <uuid>]` |
| Session model | ✅ UUIDs only (`IsCompatibleSessionName` rejects non-UUIDs); resume via `-r <uuid\|index\|latest>`; UUID captured from the first `init` stream-json frame |
| Model selection | ⚠ Hardcoded list (auto, 2.5 Pro, 2.5 Flash, 2.5 Flash-Lite, 3 Flash Preview). The CLI ships a static model registry; no live `gemini models list` command |
| Quota probe | ⚠ Identity-only — reads `~/.gemini/google_accounts.json` + `~/.gemini/settings.json`; runs a tiny headless ping to capture the default model. Daily limit / reset time deferred (see below) |
| Logging | ✅ stream-json `init` / `message` / `tool_call` / `tool_result` / `result` → marker lines via `TransformReadLine` |
| Cancellation | ✅ |
| Availability | ✅ `gemini --version` |
| Session storage | `~/.gemini/tmp/<project-slug>/chats/session-<timestamp>-<short>.json` (slug map in `~/.gemini/projects.json`) |

**Quirks.**
- The CLI prints `Warning: True color (24-bit) support not detected.` and `YOLO mode is enabled.` on **stderr**, not stdout. `TransformReadLine` lets stderr pass through unchanged so these surface as separate Activity Log lines.
- Without `--skip-trust` the CLI blocks on a workspace-trust modal that has no headless equivalent. With `--skip-trust` the CLI runs untrusted but still works for non-MCP, non-extension prompts.
- `-y` (alias `--yolo`) auto-approves all tool calls. Required for unattended runs because the default approval dialog is interactive and headless mode does not surface it.
- Quota numbers (daily limit, remaining, reset time) are fetched dynamically via `refreshAvailableCredits()` against an authenticated Google endpoint and only rendered in the interactive `/stats model` panel. There is no headless mode for it. The probe currently reports identity (email + auth type) only and surfaces an explanatory `Error` so the UI doesn't claim numbers we don't have. PTY scraping of the interactive panel is feasible but requires the workspace to be trusted *and* OAuth tokens to be hot — non-trivial in a scratch dir; deferred until the user demand justifies it.
- `--resume` accepts UUID, numeric index, or the literal `latest`. We persist UUIDs only (captured from the `init` frame) so a Codex/Claude session id can never be passed in by accident.
- The `tool_use` stream-json frame uses `tool_name` + `parameters`, not Claude's `name` + `input`. The parser handles both shapes but Gemini-specific frames are the canonical source of truth — Gemini's tool-name vocabulary is also distinct (`run_shell_command`, `read_file`, `write_file`, `replace`, `glob`, `search_file_content`, `web_fetch`, `google_web_search`).
- The init frame's session UUID is captured indirectly: `OnOutputLine` runs on the *transformed* line (after `TransformReadLine`), so we parse the marker line `● Session init <uuid> ...` rather than the raw JSON. This matters when adding new capture logic for Gemini.

**Known limitation — stdout buffering when spawned with redirected stdout.**

When the runner spawns `gemini` via `Process.Start` with `RedirectStandardOutput=true`, the CLI emits the `init` stream-json frame promptly but **buffers the remaining `message` / `tool_use` / `tool_result` / `result` frames** for the lifetime of the run, and the buffer is dropped on exit. The same invocation from a regular shell flushes correctly. Symptoms:

- Job completes with `exitCode=0`, sometimes in seconds, sometimes after a long pause.
- Activity Log shows only `● Session init <uuid> (<model>)` plus the stderr warnings.
- The captured session UUID is correct and persisted, so resume works.
- The on-disk `cli-output.log` is missing the same frames — it's a CLI-side flush issue, not a runner-side parser bug.

Fixing this requires either a PTY-based spawn (analogous to the quota probe path) or a tiny Node wrapper that does line-buffered passthrough. Tracked here; not yet implemented.

---

## 4. Adding a new CLI — checklist

Use this as a PR template. Tick each box; missing items must be justified in section 3.

- [ ] Add a constant to [`CliTypes`](../backend/Models/CliTypes.cs) (`Gemini = "gemini"`) and append it to `All`.
- [ ] Add the literal to the frontend type union in [`frontend/src/app/models/job.model.ts`](../frontend/src/app/models/job.model.ts) (`CliType`, `CLI_TYPES`).
- [ ] Implement `XxxCliService : CliExecutionServiceBase` in `backend/Services/Cli/`.
   - `CliType` returns the new constant.
   - `GetCliPath()` reads `XxxCli:Path` config with a sensible default.
   - `BuildStartInfo` composes the prompt + session + model arguments.
   - `IsCompatibleSessionName` returns the right thing — strict for UUID CLIs, `false` for sessionless CLIs.
   - `TransformReadLine` if the CLI's output needs translating to marker lines.
   - `GetModelCatalogAsync` if the CLI offers live model discovery.
- [ ] Register the service as a singleton in [`Program.cs`](../backend/Program.cs).
- [ ] Add it to [`CliRouter`](../backend/Services/Cli/CliRouter.cs)'s constructor and dispatch table.
- [ ] Add a `BuildXxxProjects()` branch in [`SessionRegistry`](../backend/Services/Cli/SessionRegistry.cs) — return `[]` if the CLI has no on-disk sessions.
- [ ] Implement `XxxQuotaProbe : QuotaProbeBase` in `backend/Services/Quota/`.
- [ ] Register the probe: `services.AddSingleton<IQuotaProbe, XxxQuotaProbe>()` in `Program.cs`.
- [ ] Add a backend xUnit test for `BuildStartInfo` argument composition.
- [ ] Add a `@billable` E2E spec `frontend/e2e/<cli>-hello-world.spec.ts`.
- [ ] Update **section 3** of this document with the real, observed behaviour and quirks.
- [ ] Update [README.md](../README.md) and [AGENTS.md](../AGENTS.md) if the new CLI changes user-visible product scope.

The order matters: get the skeleton compiling and registered first (steps 1–6) before tuning prompts/probes (steps 7+).
