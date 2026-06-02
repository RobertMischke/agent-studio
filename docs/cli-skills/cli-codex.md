---
name: cli-codex
description: Deep operational reference for the OpenAI Codex CLI driver in this project. Use when touching backend/Services/Cli/CodexCliService.cs, the codex --json frame parser, Codex session_meta capture, CodexModelDiscovery, CodexQuotaProbe, or any code that consumes Codex output. Covers invocation (note positional resume!), frame model, session-UUID capture, model catalog (live), quirks (trust prompt, /status PTY fragility), and common tasks. Pair with cli-overview for cross-CLI context.
sentinel: TASKBOARD-CLI-SKILL-CODEX-2026
---

<!-- SENTINEL: TASKBOARD-CLI-SKILL-CODEX-2026 — pickup-tests assert any CLI driving the repo can echo this back. -->

# OpenAI Codex CLI (`codex`)

OpenAI's Codex CLI. Distributed as the npm package `@openai/codex`. Headless invocation is via `codex exec`; live discovery (`CodexModelDiscovery`) keeps the model list current.

> **Source:** [`backend/Services/Cli/CodexCliService.cs`](../../backend/Services/Cli/CodexCliService.cs) (extends `CliExecutionServiceBase`).
> **Tests:** [`backend.Tests/CodexModelDiscoveryTests.cs`](../../backend.Tests/CodexModelDiscoveryTests.cs).
> **Contract:** [docs/supported-clis.md §3.2](../../docs/supported-clis.md).

## Commit / push boundary

| Question | Answer |
|---|---|
| Does this CLI commit on its own? | **No.** The platform owns the commit boundary. |
| Does this CLI push on its own? | **No.** Push is the runner's job (today: a tracked gap). |
| What if it does? | Regression. Raise an issue and cite [docs/commit-push-doctrine.md](../commit-push-doctrine.md). |

Codex `exec` runs unattended and may inspect the working tree with `git status` / `git diff`. That is fine. What it must never do: `git commit`, `git push`, `git amend`, `git checkout`, `git reset --hard`, or any branch-mutating command. The runner records the commit on the `3-progress -> 4-review` transition; see [docs/commit-push-doctrine.md](../commit-push-doctrine.md) and [ADR-0019](../architecture-decisions.md#adr-0019---platform-owns-the-commit-boundary-2026-05-04).

## Identity card

| | Value |
|---|---|
| Binary | `codex` |
| Config key | `CodexCli:Path` (override) |
| Version probe | `codex --version` |
| Output mode used | `--json` (NDJSON, one frame per line; **not** the same shape as Claude/Gemini's stream-json) |
| Session ids | UUID — strict |
| Resume flag | `exec resume <uuid>` (positional, before `--json`) |
| Session storage | `~/.codex/session_index.jsonl` + `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl` |
| Quota probe | `/status` PTY probe — 5-hour + weekly buckets (Codex reports % left, we invert to % used) |

## Invocation reference

### Fresh run

```sh
codex exec --json [-m <model>] -    # then write prompt to stdin, close
```

The orchestrator passes `-` as the positional and pipes the full prompt
(system-prefix + rendered template) over the redirected stdin pipe, then
closes the pipe so Codex sees EOF. `-m` selects the model. `--json` makes
stdout machine-readable; without it we cannot extract the session UUID.

**Why stdin, not positional argv** (codex 0.130+): handing the prompt as the
last positional argv made the model treat the entire block as system-side
"initial instructions" and reply with `[[TASK_NOOP]]` ("no task provided")
on every fresh job. See `docs/codex-runner-investigation.md` for the
forensic write-up.

### Resume

```sh
codex exec resume <uuid> --json [-m <model>] -   # prompt via stdin
```

`resume` is a **subcommand of `exec`**, taking the UUID positionally. Don't pass it as `--resume=<uuid>` (that's Copilot's flag) and don't pass it as `-r <uuid>` (that's Claude/Gemini). Codex's `exec resume <uuid>` is positional. The prompt itself goes over stdin via the same `-` switch as fresh runs.

### Anti-patterns

- **Don't** swap argument order. `codex exec --json resume <uuid> -` parses `resume` as the prompt sentinel.
- **Don't** pass a non-UUID session id. `IsCompatibleSessionName` rejects non-UUIDs to keep cross-CLI session names from leaking through.
- **Don't** revert to positional-argv prompt delivery. `BuildStartInfo_LongPromptKeepsPromptOutOfArgvAndUsesStdin` locks the stdin path; `docs/codex-runner-investigation.md` records why.

### System-prompt prefix

Codex has no `--append-system-prompt` flag, so `CodexCliService.BuildSystemPromptPrefix` prepends a short orchestrator note to the stdin payload on every invocation (fresh runs and resumes). The prefix carries two prophylactic hints:

1. **Sentinel reminder.** Repeats the `[[TASK_DONE]] / [[TASK_BLOCKED:...]] / [[TASK_NEEDS_INPUT:...]] / [[TASK_NOOP]]` grammar. On a resume turn the fresh-start template is not re-rendered, so without this the agent regularly drops the terminal sentinel and the run lands in auto-review as "missing-terminal-sentinel".
2. **Windows no-shell hint** (only when `OperatingSystem.IsWindows()`). Tells Codex not to retry on `windows sandbox: runner error` / `CreateProcessAsUserW failed` and to surface `[[TASK_BLOCKED:windows-sandbox]]` instead. This is the preventive complement to `AgentEnvironmentDetector`'s reactive in-stream match.

Keep the prefix short — the length-guard test in `CodexCliServiceTests.BuildSystemPromptPrefix_StaysShort` enforces an upper bound because every Codex invocation pays this in tokens.

## `--json` frame model

Codex emits JSON Lines on stdout. `TransformReadLine` delegates to the pure
[`CodexOutputRenderer`](../../src/AgentTaskboard.Runner/Cli/Rendering/CodexOutputRenderer.cs)
(the marker-line twin of `CodexEventAdapter`; see `cli-overview` § "Unified renderer
layer"), which maps each frame onto the **same** marker vocabulary Claude emits, so
a Codex run reads as cleanly as a Claude run in the Activity Log.

| Frame | Marker out | Stream |
|---|---|---|
| `{"type":"thread.started","thread_id":"<uuid>"}` | `● Session <uuid>` | stdout |
| `{"type":"session_meta","payload":{"id":"<uuid>"}}` (legacy; `session_id` on root also accepted) | `● Session <uuid>` | stdout |
| `{"type":"turn.started"}` | *(suppressed)* | — |
| `{"type":"turn.completed","usage":{…}}` | `● Turn completed (tokens: <input+output>)` | stdout |
| `{"type":"turn.failed","error":{"message":…}}` | `● Turn failed: <reason>` | stderr |
| `{"type":"item.started",…}` | *(suppressed — `item.completed` renders the same item)* | — |
| `item.completed` `agent_message` | model text, multi-line split | stdout |
| `item.completed` `reasoning` | *(suppressed, like Claude's `thinking`)* | — |
| `item.completed` `command_execution` / `command_call` / `local_shell_call` | `● Run <cmd>` | stderr iff `exit_code != 0` |
| `item.completed` `file_change` | `● Edit <path>` | stdout |
| `item.completed` `web_search` | `● Search web <query>` | stdout |
| `item.completed` `update_plan` / `todo` | `● Todo update` | stdout |
| any other frame / item type | `● <type>` (never raw JSON) | stdout |

**Deliberate equivalences (not byte-identical to Claude).** Codex's frame catalogue
differs from Claude's, so the marker *text* differs, but each maps to a verb the
frontend `classifyAction` already buckets: `● Session` (vs Claude `● Session init`),
`● Turn completed (tokens: N)` (Codex analogue of Claude's `● Result (success)`), and
`● Run`/`● Edit`/`● Search web`/`● Todo update` reuse Claude's verbs verbatim. The
AC's literal `"Tool: pwsh.exe"` shape was **not** used: it would classify as `other`,
not `command`. Frame→marker snapshots are locked in `backend.Tests/CodexOutputRendererTests.cs`.

## Session-UUID capture

Capture runs in `MapLineToRunEvents` (via `TryCaptureSessionId`), **not** in
`OnOutputLine`. The base class invokes `MapLineToRunEvents` on the **raw** stdout
line but `OnOutputLine` on the **transformed** line; now that `TransformReadLine`
rewrites `thread.started` → `● Session <id>`, the original `thread_id` payload is
gone by the time `OnOutputLine` fires. `MapLineToRunEvents` is the same raw-line hook
where `TryCaptureTurnUsage` already lives, so both telemetry captures read real JSON.

```csharp
protected override IEnumerable<CliRunEvent> MapLineToRunEvents(string jobKey, CliOutputLine line)
{
    if (line.Stream != "stdout") return Array.Empty<CliRunEvent>();
    if (_processes.TryGetValue(jobKey, out var info))
    {
        TryCaptureTurnUsage(info, line);
        TryCaptureSessionId(info, line);   // reads the raw thread.started / session_meta frame
    }
    return CodexEventAdapter.Map(line.Text, jobKey);
}
```

`TryExtractSessionId(string?)` (the pure parser, with 7 regression tests) is unchanged:
it accepts `thread.started.thread_id` (preferred) or legacy `session_meta` `payload.id` /
`session_id`, gated on a canonical UUID. **Anti-pattern:** do not move capture back into
`OnOutputLine` — it would parse the `● Session` marker text instead of the JSON and break.

## Stale Codex sessions

Codex is the second reference path for stale-session reliability after Claude. The same product invariant applies: a successful `codex exec resume <uuid>` is necessary, but not sufficient. The resumed turn must act on the latest user follow-up and reconcile against current job-folder evidence.

Codex has a stronger structured-protocol story than Claude: the cloned `openai-codex` reference contains an App Server protocol over JSON-RPC, and ADR-0013 points the future adapter in that direction. Until that migration exists, the current `codex exec --json` path must still prove three things:

1. `session_meta.payload.id` is captured and persisted into `sessionChain`.
2. `exec resume <uuid> --json -` (prompt over stdin) continues the intended conversation rather than starting fresh.
3. If a resume target is rejected or produces no useful work, the runner routes through Recovery and re-issues the user follow-up once with stronger framing.

Next stale-session probes for Codex should mirror Claude's: fresh run, short resume, backend-restart resume, deliberately missing session id, and accepted stale resume with an observable edit or protocol update.

## Model handling — live discovery

[`CodexModelDiscovery`](../../backend/Services/Cli/CodexModelDiscovery.cs) queries the CLI for its current model list and caches the result. `GetModelCatalogAsync` is a thin wrapper.

To refresh the cache, the user clicks the side-sheet refresh button which calls `/api/cli/codex/models?forceRefresh=true`.

When a CLI version bump changes the output format, the regression shows up as `Source = "live-discovery-failed"` in the catalog and the dropdown empties. Tests in `CodexModelDiscoveryTests.cs` lock the parser shape.

## Quirks (and what to do about them)

1. **Trust prompt has "1. Yes, continue" pre-selected and accepts a bare Enter.** Sending `1<Enter>` works but leaves a stray `1` in the input box that prefixes the next slash command. Use `<Enter>` alone when scripting Codex over a PTY (the quota probe does this).
2. **`/status` PTY probe is fragile.** Trust + welcome + `/status` is a chained multi-step probe; one extra prompt or layout shift breaks it. See comments in [`CodexQuotaProbe`](../../backend/Services/Quota/CodexQuotaProbe.cs). When updating, capture the new PTY transcript under `backend.Tests/Fixtures/quota/codex/` and lock with a fixture-based test.
3. **Codex reports % left, we report % used.** The probe inverts the value so the UI's `UsedPct` semantics stay consistent across CLIs. Don't double-invert.
4. **`--json` is required.** Without it, stdout is a colored panel that can't be parsed. The runner always passes it.

## Watchdog parity with Claude (ADR-0030)

The watchdog tunings shipped for Claude in ADR-0030 are CLI-agnostic and apply unchanged here:

- **`SessionInitializing` budget is 60 s suspicious / 120 s hung.** Codex's first frame (`{"type":"session_configured", …}`) sometimes lags 30-50 s behind spawn under API load; the old 30 / 60 budget killed those legitimately-slow inits.
- **`Unknown` frames count as activity.** A future Codex `--json` frame variant that the adapter does not yet classify still resets the silence clock; the unknown-sample is captured for diagnosis.
- **Loud-failure routing on N same-job kills.** When the same Codex job fails three runs in a row, the runner moves it to `5-human-review` (instead of leaving it stuck in `3-progress` while auto-mode flips to `manual`).
- **`logs/tool-calls.jsonl`** is written for any CLI driver — Codex's `tool_use` events flow through `CodexEventAdapter.MapLineToRunEvents` to `CliRunEvent.ToolStarted` / `ToolCompleted` and land in the same per-job JSONL the operator playbook for Claude references.

Codex-specific differences worth flagging during a hang:

- Codex `--json` frames have two parallel surfaces: the typed-event `CodexEventAdapter` (what the watchdog reads) and the marker-line `CodexOutputRenderer` (what the Activity Log reads). They are independent pure mappers over the same frames; patching one does not touch the other. If you patch frame parsing, run the deterministic suite in [`backend.Tests/CliWatchdogIntegrationTests.cs`](../../backend.Tests/CliWatchdogIntegrationTests.cs), the typed-event tests in [`backend.Tests/CodexEventAdapterTests.cs`](../../backend.Tests/CodexEventAdapterTests.cs), and the marker-line tests in [`backend.Tests/CodexOutputRendererTests.cs`](../../backend.Tests/CodexOutputRendererTests.cs).
- Codex does not emit Claude's `rate_limit_event` shape; the rate-limit-aware budget multiplier (an ADR-0030 follow-up) will need its own probe before it can flip on for Codex runs.
- Codex has no equivalent of Claude's `~/.claude/projects/<cwd>/<uuid>.jsonl` side-channel session file. The heartbeat helper documented for Claude does not have a Codex analogue today; the same pipe-buffer hypothesis would need a different signal (e.g. polling `codex` IPC or a process-level CPU heartbeat).

## Quota probe

[`CodexQuotaProbe`](../../backend/Services/Quota/CodexQuotaProbe.cs) returns two windows: a 5-hour bucket and a weekly bucket. Implementation runs `codex` over a PTY, accepts the trust prompt, navigates to `/status`, scrapes the panel.

The probe reports `% used` (1 - `% left`). Source string is `/status (PTY)`.

## Common tasks

### "Add a marker mapping for a new Codex frame / item type"

The `TransformReadLine` translation now exists in
[`CodexOutputRenderer`](../../src/AgentTaskboard.Runner/Cli/Rendering/CodexOutputRenderer.cs).
To extend it for a new frame or `item.type`:

1. Capture the real `--json` frame from `.runtime/cli-output/codex-*.jsonl`.
2. Add a `case` to `CodexOutputRenderer.Render` (top-level frame) or `RenderItem`
   (item type), mapping to an existing marker in `cli-overview` § "Marker-line
   vocabulary". Do not invent a new marker shape — pick `read`/`search`/`command`/`edit`.
3. Add a snapshot test to `backend.Tests/CodexOutputRendererTests.cs` (frame in → marker out).
4. Session capture stays in `MapLineToRunEvents` on the raw JSON — never move it onto
   the rendered marker line.

### "Codex isn't resuming"

1. Verify the persisted `sessionName` in `job.json` is a UUID.
2. Verify the `exec resume <uuid> --json -` argument order (prompt arrives over stdin).
3. Verify the captured UUID matches what's on disk in `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl`.

### "Live model discovery returned an empty list"

1. Check `CodexModelDiscoveryTests.cs` for the latest expected output format.
2. Run `codex models list` (or whatever the current command is) by hand to capture current output.
3. Update the parser; lock with a new fixture row.

### "Add a regression test for a new frame shape"

1. Capture the raw JSON from `~/.runtime/cli-output/codex-*.jsonl`.
2. Save under `backend.Tests/Fixtures/cli/codex/<name>.jsonl`.
3. Add a `CodexCliServiceTests` that asserts whatever invariant holds.

## Fixtures

`backend.Tests/Fixtures/cli/codex/` holds raw `--json` frames. Keep one fixture per concern (one for session_meta, one for tool calls, etc.) so adding a new behaviour doesn't pollute existing assertions.
