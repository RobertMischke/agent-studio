# Supported CLIs

This document is the **contract** every CLI integration in the agent-orchestrator must satisfy. It describes:

1. What "supported" means.
2. The capabilities every supported CLI must provide.
3. The CLIs supported today and their known quirks.
4. The step-by-step procedure for adding a new CLI.

Whenever a CLI integration is added, changed, or audited, this file is updated **in the same PR**.

> **Language:** English. See [AGENTS.md](../../AGENTS.md#documentation-language).

> **Operational knowledge** (frame catalogues, capture flows, known incidents, common tasks) lives in the per-CLI skills under [`docs/cli/skills/`](skills): [`cli-overview`](skills/cli-overview.md), [`cli-claude`](skills/cli-claude.md), [`cli-codex`](skills/cli-codex.md), [`cli-gemini`](skills/cli-gemini.md). This document is the contract; the skills are the working notes. Both must stay in sync.

---

## 1. What "supported" means

A "supported CLI" is a coding-agent CLI (Claude Code, Codex, Gemini, …) that the task processor can drive end-to-end:

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

**Code.** Subclass [`CliExecutionServiceBase`](../../backend/Services/Cli/CliExecutionServiceBase.cs) and implement `BuildStartInfo`. The base class handles spawning, streaming, cancellation, persistence, and reattach.

**Test.** A backend xUnit test that spawns the CLI in a temp directory and asserts a non-empty output line + clean exit. An E2E `@billable` "hello world" Playwright spec that drives the UI.

### 2.2 Session model

**Contract.** The driver decides whether the CLI has a usable session concept. If it does, it must:

- Define `IsCompatibleSessionName(string?)` strictly — reject ids that came from a different CLI (a UUID handed to a CLI that expected a different id shape, or a leftover slug from the removed Copilot driver). Accepting an alien id leads to silent hangs.
- Build the resume command in `BuildStartInfo` when `resumeSession=true`.
- Optionally capture the session id from CLI output in `OnOutputLine` (Codex pattern).

If the CLI has no usable session concept, `IsCompatibleSessionName` returns `false` for everything and the UI Continue button stays disabled automatically.

**Session storage discovery.** [`SessionRegistry`](../../backend/Services/Cli/SessionRegistry.cs) reads each CLI's on-disk session store to populate the Sessions side-sheet. New CLIs add their own `BuildXxxProjects()` method here. Disk reads are best-effort — missing files mean "no sessions", not an error.

**Session loss is an expected state.** A previously-captured session id can disappear between runs (user pruned the store, CLI upgrade rotated the slug format, retention expired, machine switch). The product handles this via graceful Recovery, not as a hard failure (ADR-0002 / ADR-0006). The driver's job is therefore minimal: when the CLI rejects the resume target, just don't capture a new session id. [`ProjectRunner.OnCliFinishedAsync`](../../backend/Services/Runner/ProjectRunner.cs) detects "resume attempted, no new id captured", clears `info.SessionName`, marks the chain as recovery, and writes a single `[capture-fail]` decision message into the chat. The next user follow-up routes through Recovery automatically. **Don't** keep a known-dead session id in `SessionName`; the next follow-up will then re-issue the same dead resume and produce an identical error. **Don't** map the CLI's session-not-found message to a hard failure status — the orchestrator already explains it once and the auto-rebuild is the design.

**Stale sessions are a separate quality risk.** A session can still exist on disk and be accepted by the CLI while its useful context has degraded after a long idle period, provider-side cache eviction, prompt-harness changes, or a partially-applied resume optimization. The [April 23, 2026 Anthropic postmortem](https://www.anthropic.com/engineering/april-23-postmortem) is the reference incident: a Claude Code harness optimization for sessions idle over one hour accidentally kept clearing older thinking on every later turn, making resumed sessions forgetful and repetitive even though the model and API were fine. Our contract is therefore stronger than "resume command exits zero": a resumed run must still act on the user follow-up, preserve task intent, and produce useful evidence. When stale-session behavior is suspected, add tests at the runner/recovery layer first, then CLI-specific live probes for Claude and Codex.

### 2.3 Model selection

**Contract.** `GetModelCatalogAsync` returns a list of models the user can pick from. Acceptable sources, in preference order:

1. **Live discovery** — query the CLI for its current model list (a PTY-driven probe is the reference pattern).
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

**Implementation pattern.** Most probes spawn the CLI in a scratch dir under `%TEMP%/agent-taskboard-quota/<cliType>` via a PTY, send slash-commands, scrape the rendered panel. See `ProbeWithStepsAsync` in [`QuotaProbeBase`](../../backend/Services/Quota/QuotaProbeBase.cs).

**Aggregation.** [`QuotaService`](../../backend/Services/Quota/QuotaService.cs) aggregates all registered `IQuotaProbe`s and serves `/api/cli/quota`. New CLIs register via `services.AddSingleton<IQuotaProbe, XxxQuotaProbe>()` in `Program.cs`.

**Refresh cadence.** Background refresh is automatic; the user can force a refresh per-CLI via the side-sheet button.

### 2.5 Logging & Activity Log

**Contract.** Output the user sees in the Activity Log must be:

- **Streamed,** not buffered until the run finishes.
- **Marker-line formatted** so the frontend's `activity-log.parser` can classify entries (Read/Search/Edit/Run/Todo/Task/Messages).
- **Free of ANSI escapes** in the persisted form (the base class strips them on write).
- **UTF-8 safe** — the base class forces UTF-8 stdout/stderr encoding; do not override.

**Implementation.** Override `TransformReadLine(CliOutputLine raw)` to translate the CLI's native output into marker lines. Reference: [`ClaudeCliService.TransformReadLine`](../../backend/Services/Cli/ClaudeCliService.cs) — it expands stream-json NDJSON into `● Read /path`, `● Edit /path`, `● Run <cmd>`, etc.

If the CLI emits plain text already in a parser-friendly shape, `TransformReadLine` can be the default identity transform.

### 2.6 Cancellation

**Contract.** A running job must be cancellable from the UI. The base class kills the process tree on cancel.

**CLI-specific cleanup.** If the CLI leaves orphaned PTY children, modal pickers, or background helpers, the driver is responsible for cleaning them up. Quota probes additionally send `<Esc><Esc>` before tearing down to close any open modal pickers — keep doing this for new CLIs.

### 2.7 Availability & authentication check

**Contract.** `TestCliPath()` returns `(Available, Version, ResolvedPath)`. Default implementation runs `<cli> --version` and parses the first non-empty line. Override only if the CLI's version surface differs.

**Authentication.** Most CLIs auth out-of-band (browser login, env var, gh-cli token). The task processor does **not** drive login flows — if `TestCliPath` succeeds but the CLI is logged out, the failure surfaces when the first quota probe or job starts. New CLIs should make the failure mode obvious in their error message.

### 2.8 Execution context (read-only observability)

**Contract.** Every CLI loads context beyond the prompt the runner hands it — a memory / instruction-file chain walked up from the working directory, a session/transcript store, a global config directory, and (Claude) wired-in MCP servers. `DescribeContextSources(string jobKey)` returns a `CliExecutionContext` describing those sources for the live (or just-finished, still-tracked) run, plus the scalar header (model, effective permission mode, cwd). This is a **read-only** surface (ASS-1739 / T1a): producing it must never change what the CLI loads — policy changes are a separate task (T1b). The base implementation derives everything from the adapter invocation plus each CLI's documented config-path conventions and sets `Source = "convention"`; a driver with a richer self-report (Claude's stream-json `init` frame) overrides `DescribeContextSources`, merges the init-frame model / permission mode / cwd / MCP list on top, and sets `Source = "init-frame"`. Returns `null` for an unknown run; the interface default is a no-op so test stubs and not-yet-wired drivers stay compilable.

**Code.**
- Model: [`CliExecutionContext` / `CliContextSource` / `CliContextSourceKinds`](../../backend/Shared/Models/CliExecutionContext.cs).
- Contract + base/convention implementation: [`ICliExecutionService.DescribeContextSources`](../../backend/Features/Cli/Execution/ICliExecutionService.cs) and [`CliExecutionServiceBase`](../../backend/Features/Cli/Execution/CliExecutionServiceBase.cs) (`BuildConventionContext`).
- Pure convention builder (per-CLI path conventions, filesystem-probed): [`CliContextConventions`](../../backend/Features/Cli/Execution/CliContextConventions.cs).
- Claude init-frame override + parser: [`ClaudeCliService.DescribeContextSources`](../../backend/Features/Cli/Execution/ClaudeCliService.cs) and [`ClaudeInitContextParser`](../../backend/Features/Cli/Execution/Adapters/ClaudeInitContextParser.cs).
- Persistence: the runner calls `DescribeContextSources` at run finish (while the per-run `ProcInfo` is still alive) and stamps the result onto the latest `SessionEvent` via [`TaskSessionLog.BackfillLatestSessionEventExecutionContext`](../../backend/Features/Tasks/TaskSessionLog.cs); the run-detail "Execution Context" panel and a slim timeline marker read it back (`RunTimeline`).

**Test.** `CliContextConventionsTests` (pure path conventions), `ClaudeInitContextParserTests` (init-frame parse), `DescribeContextSourcesTests` (service wiring for the convention CLIs + untracked-run null), `SessionEventsTests.BackfillLatestSessionEventExecutionContext_*` (durable JSONL round-trip), and `RunTimelineBuilderTests.ExecutionContext_IsCarriedFromEventOntoRunRecord` (timeline projection) together cover the producer → persistence → projection data path.

**New CLI note.** The convention branch in `CliContextConventions.For` is the only thing a new sessionless/init-frame-less CLI needs; add its memory-file name and config-path conventions there. A driver that emits its own startup frame can override `DescribeContextSources` like Claude does.

### 2.9 Context mode — clean vs. shared (per-run isolation)

**Contract.** A run executes in one of two **context modes** (T1b / ASS-1742):

- **`clean`** (the default for coding runs) — the run sees only the prompt plus the versioned repository files (`AGENTS.md` / `CLAUDE.md` and friends, which are committed and live in the working tree). It does **not** see the operator's accumulated global CLI state: user-level memory, prior session transcripts, or scratch config. This makes a run reproducible — two operators on the same commit get the same context.
- **`shared`** — the run reuses the operator's global CLI state (whatever lives under `~/.claude`, `~/.codex`, …). Pick it deliberately when a run is *meant* to lean on accumulated local context.

`clean` is **not a CLI flag** — no supported CLI exposes "ignore my global state" as a switch. Each adapter implements it by relocating the CLI's whole config home to a freshly created per-run temp directory, seeding only the auth + base-config files the CLI needs to run, and pointing the CLI at that temp home via an environment override. Repo instruction files are untouched in both modes because they live in the working tree, not the config home.

**Per-CLI mechanism.**

| CLI | `SupportsCleanContext` | Mechanism | Seeded into the temp home |
|-----|:---:|-----------|---------------------------|
| Claude | ✅ | `CLAUDE_CONFIG_DIR` → per-run temp dir | `.credentials.json`, `settings.json` (excludes `CLAUDE.md`, `projects/`) |
| Codex | ✅ | `CODEX_HOME` → per-run temp dir | `auth.json`, `config.toml` (excludes `history.jsonl`) |
| Gemini / Antigravity | ❌ shared-only | agentapi driver exposes no documented home override | — |

A CLI with no isolation mechanism honestly **declares `shared-only`** (`SupportsCleanContext => false`). A run that requested `clean` against a shared-only CLI is stamped `contextMode = "shared"` so the read-only Execution Context panel (§2.8) shows the truth rather than a mode the CLI couldn't honor.

**Auth is the adapter's duty.** Seeding only auth + base config (never history/memory) is what lets a clean run still log in. If auth comes from an env var instead (`ANTHROPIC_API_KEY`), a missing seed file is non-fatal — the clean home is still created and the env override still points at it.

**Lifetime.** The per-run temp home is owned by the run's `ProcInfo` and torn down when that is evicted (not at process exit), so the async `DescribeContextSources` call at run-finish can still read the seeded paths. Teardown is best-effort and idempotent.

**Configuration & resolution.** Context mode is set per project (with an optional per-task override), defaulting to `clean`. Resolution precedence is **task override → project setting → `clean` default**, narrowed to `shared` when the resolved CLI is shared-only. Project endpoints: `GET/PUT /api/projects/{name}/cli-context-mode(s)`; the project-detail settings UI carries the recommendation verbatim: *"Empfehlung: clean - der Run sieht nur Prompt + versionierte Repo-Dateien; reproduzierbar. shared nur bewusst waehlen."*

**Code.**
- Vocabulary + resolution: [`CliContextModes`](../../backend/Shared/Models/CliContextModes.cs) (`Normalize` defaults to `clean`, `SupportsClean`), [`ProjectSettingsService`](../../backend/Features/Projects/ProjectSettingsService.cs) (`SetCliContextMode` / `ResolveContextMode`).
- Per-run preparer + handle: [`CleanContextPreparer` / `CleanContextPreparation`](../../backend/Features/Cli/Execution/CleanContextPreparation.cs) (`PrepareClaude` / `PrepareCodex` return a disposable preparation that owns the temp home).
- Adapter hook + env injection: `SupportsCleanContext` / `PrepareCleanContext` on [`CliExecutionServiceBase`](../../backend/Features/Cli/Execution/CliExecutionServiceBase.cs) (clean home built and env overrides applied in `StartAsync`); Claude / Codex overrides in their adapters.

**Test.** `CliContextModesTests` (vocabulary defaults + `SupportsClean` matrix; `CleanContextPreparer` seeds only the allow-listed files, sets the right env var, surfaces the temp paths as sources, and tears the home down on dispose) and `ProjectSettingsServiceTests` (resolution precedence, shared-only reporting, override persistence) cover the policy + isolation path.

**New CLI note.** Default a new CLI to **shared-only** (inherit the base `SupportsCleanContext => false`). Implement `clean` only when the CLI has a real config-home env override (like `CLAUDE_CONFIG_DIR` / `CODEX_HOME`): add a `PrepareXxx` to `CleanContextPreparer` that seeds only auth + base config, then override `SupportsCleanContext => true` and `PrepareCleanContext` on the adapter. Never fake `clean` by passing a flag the CLI doesn't honor — declaring shared-only honestly is correct.

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
- Trust + welcome + `/status` is a fragile multi-step probe; see comments in [`CodexQuotaProbe`](../../backend/Services/Quota/CodexQuotaProbe.cs).

### 3.3 Gemini CLI (`gemini`)

Verified against `@google/gemini-cli` v0.39.1.

| Aspect | Status |
|--------|--------|
| Process lifecycle | ✅ `gemini -p "<prompt>" -o stream-json --skip-trust -y [-m <id>] [-r <uuid>]` |
| Session model | ✅ UUIDs only (`IsCompatibleSessionName` rejects non-UUIDs); resume via `-r <uuid\|index\|latest>`; UUID captured from the first `init` stream-json frame |
| Model selection | ⚠ Hardcoded list (auto, 2.5 Pro, 2.5 Flash, 2.5 Flash-Lite, 3 Flash Preview). The CLI ships a static model registry; no live `gemini models list` command |
| Quota probe | ✅ PTY drive of `/stats model` panel — parses tier, email, used %, daily limit, reset time. Sends a one-char prompt before `/stats model` so the panel renders QuotaStatsInfo (see below) |
| Logging | ✅ stream-json `init` / `message` / `tool_call` / `tool_result` / `result` → marker lines via `TransformReadLine` |
| Cancellation | ✅ |
| Availability | ✅ `gemini --version` |
| Session storage | `~/.gemini/tmp/<project-slug>/chats/session-<timestamp>-<short>.json` (slug map in `~/.gemini/projects.json`) |

**Quirks.**
- The CLI prints `Warning: True color (24-bit) support not detected.` and `YOLO mode is enabled.` on **stderr**, not stdout. `TransformReadLine` lets stderr pass through unchanged so these surface as separate Activity Log lines.
- Without `--skip-trust` the CLI blocks on a workspace-trust modal that has no headless equivalent. With `--skip-trust` the CLI runs untrusted but still works for non-MCP, non-extension prompts.
- `-y` (alias `--yolo`) auto-approves all tool calls. Required for unattended runs because the default approval dialog is interactive and headless mode does not surface it.
- Quota numbers (daily limit, remaining, reset time) are fetched dynamically via `refreshAvailableCredits()` against an authenticated Google endpoint and only rendered in the interactive `/stats model` panel. The probe drives the panel through a PTY: it pre-trusts a scratch folder via `~/.gemini/trustedFolders.json`, spawns gemini with `-m auto-gemini-3 --skip-trust`, dismisses the IDE / multiline setup modals if they appear, sends a one-character prompt (so `activeModels.length > 0` and the panel renders QuotaStatsInfo), then sends `/stats model` and parses the panel. Cost: one tiny generation call per probe, bounded by `Quota:TtlSeconds` (default 600). Free-tier accounts get identity only — the panel doesn't render quota lines there.
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

- [ ] Add a constant to [`CliTypes`](../../backend/Models/CliTypes.cs) (`Gemini = "gemini"`) and append it to `All`.
- [ ] Add the literal to the frontend type union in [`frontend/src/app/models/job.model.ts`](../../frontend/src/app/models/job.model.ts) (`CliType`, `CLI_TYPES`).
- [ ] Implement `XxxCliService : CliExecutionServiceBase` in `backend/Services/Cli/`.
   - `CliType` returns the new constant.
   - `GetCliPath()` reads `XxxCli:Path` config with a sensible default.
   - `BuildStartInfo` composes the prompt + session + model arguments.
   - `IsCompatibleSessionName` returns the right thing — strict for UUID CLIs, `false` for sessionless CLIs.
   - `TransformReadLine` if the CLI's output needs translating to marker lines.
   - `GetModelCatalogAsync` if the CLI offers live model discovery.
- [ ] Register the service as a singleton in [`Program.cs`](../../backend/Program.cs).
- [ ] Add it to [`CliRouter`](../../backend/Services/Cli/CliRouter.cs)'s constructor and dispatch table.
- [ ] Add a `BuildXxxProjects()` branch in [`SessionRegistry`](../../backend/Services/Cli/SessionRegistry.cs) — return `[]` if the CLI has no on-disk sessions.
- [ ] Add a `Xxx(cwd, home)` branch in [`CliContextConventions`](../../backend/Features/Cli/Execution/CliContextConventions.cs) for the read-only execution-context surface (§2.8) — the CLI's memory-file name and config-path conventions. Override `DescribeContextSources` only if the CLI emits its own startup frame.
- [ ] Decide the **context mode** support (§2.9): leave the base `SupportsCleanContext => false` (shared-only) unless the CLI has a real config-home env override. If it does, add a `PrepareXxx` to [`CleanContextPreparer`](../../backend/Features/Cli/Execution/CleanContextPreparation.cs) (seed only auth + base config) and override `SupportsCleanContext` / `PrepareCleanContext` on the adapter. Never fake `clean` with a flag the CLI doesn't honor.
- [ ] Implement `XxxQuotaProbe : QuotaProbeBase` in `backend/Services/Quota/`.
- [ ] Register the probe: `services.AddSingleton<IQuotaProbe, XxxQuotaProbe>()` in `Program.cs`.
- [ ] Add a backend xUnit test for `BuildStartInfo` argument composition.
- [ ] Add a `@billable` E2E spec `frontend/e2e/<cli>-hello-world.spec.ts`.
- [ ] Update **section 3** of this document with the real, observed behaviour and quirks.
- [ ] Update [README.md](../../README.md) and [AGENTS.md](../../AGENTS.md) if the new CLI changes user-visible product scope.

The order matters: get the skeleton compiling and registered first (steps 1–6) before tuning prompts/probes (steps 7+).
