---
name: cli-claude
description: Deep operational reference for the Anthropic Claude Code CLI driver in this project. Use when touching backend/Services/Cli/ClaudeCliService.cs, the stream-json frame parser, Claude session capture, the rate-limit pill, ClaudeQuotaProbe, or any code that consumes Claude's output. Covers invocation, frame catalogue, session-UUID capture, model normalisation, quirks (stdin EOF, npm shim), known incidents, and common tasks. Pair with cli-overview for cross-CLI context.
sentinel: TASKBOARD-CLI-SKILL-CLAUDE-2026
---

<!-- SENTINEL: TASKBOARD-CLI-SKILL-CLAUDE-2026 — pickup-tests assert any CLI driving the repo can echo this back. -->

# Claude Code CLI (`claude`)

Anthropic's official Claude Code CLI. Distributed as the npm package `@anthropic-ai/claude-code`. The most feature-rich of our four CLIs and the one we lean on most.

> **Source:** [`backend/Services/Cli/ClaudeCliService.cs`](../../backend/Services/Cli/ClaudeCliService.cs) (extends `CliExecutionServiceBase`).
> **Tests:** [`backend.Tests/ClaudeQuotaProbeTests.cs`](../../backend.Tests/ClaudeQuotaProbeTests.cs) (probe). Driver-level `TransformReadLine` tests live alongside fixtures under `backend.Tests/Fixtures/cli/claude/` (see § "Fixtures").
> **Contract:** [docs/supported-clis.md §3.1](../../docs/supported-clis.md).

## Identity card

| | Value |
|---|---|
| Binary | `claude` (npm-installed). On Windows the shim resolves to `node_modules/@anthropic-ai/claude-code/bin/claude.exe` via `PATHEXT`. |
| Config key | `ClaudeCli:Path` (override) |
| Version probe | `claude --version` |
| Output mode used | `stream-json` (NDJSON, one frame per line) |
| Session ids | UUID (`8-4-4-4-12`) — strict |
| Resume flag | `-r <uuid>` |
| Rate limit telemetry | `rate_limit_event` frames per turn (Claude-only) |
| Session storage | `~/.claude/projects/<encoded-cwd>/<uuid>.jsonl` |
| Quota probe | `/usage` PTY probe — session bucket + weekly bucket |

## Invocation reference

### Fresh run

```sh
claude -p "<prompt>" \
  --output-format stream-json --verbose --dangerously-skip-permissions \
  [--model <id>] \
  [--append-system-prompt-file <path>]
```

`-p "<prompt>"` is the headless / "print" mode; output goes to stdout, the process exits when the model is done. `--verbose` is *required* alongside `stream-json` because Claude's CLI rejects the combo without it. `--dangerously-skip-permissions` auto-approves tool calls — required for unattended runs.

`--append-system-prompt-file <path>` injects [`agent-rules/core.md`](../../agent-rules/core.md) as a system-prompt overlay. It's a *file* flag (not the inline string flag) so the multi-line markdown stays out of the command-line argument and lets the Anthropic CLI cache the system-prompt portion across runs. We resolve the path via `ResolveAgentRulesPath` (looks at config + walks up from `AppContext.BaseDirectory`); files larger than 8 KB are skipped with a warning to avoid blowing the system-prompt cache.

### Resume

```sh
claude -r <session-uuid> -p "<prompt>" \
  --output-format stream-json --verbose --dangerously-skip-permissions \
  [--model <id>] \
  [--append-system-prompt-file <path>]
```

`-r` accepts only the canonical UUID written by Claude's CLI itself. There is no equivalent of `--name` in current versions — sessions are identified by the UUID emitted in the first `system` `stream-json` frame.

The runner never pre-generates a slug for Claude (that's a Copilot pattern). On a fresh run we pass no `-r`, then capture the UUID from the first frame and write it back to `job.json` so the next continuation can resume.

### Anti-patterns

- **Don't** pass `-r <slug>`. Claude's `-r` doesn't error — it hangs waiting for a session that doesn't exist. `IsCompatibleSessionName` rejects everything that isn't the canonical UUID precisely because of this.
- **Don't** combine `-p` with no `--output-format`. The default text format buffers the full reply until the model finishes, so the Activity Log stays empty for 30+ s on long replies. `stream-json` flushes per frame.
- **Don't** drop `--verbose`. Without it, Claude's CLI silently exits with status 1 when `-p` is combined with `stream-json`.

## Stream-json frame catalogue

Each line of stdout is a JSON object. `TransformReadLine` switches on the `type` field. The shapes below are verified against the live CLI; when adding a new branch, capture a fixture under `backend.Tests/Fixtures/cli/claude/` and lock it with a test.

| `type` | Purpose | Renders to | Action |
|---|---|---|---|
| `system` | Init / health frame. Carries `session_id` and `subtype` (`init` etc.). | `● Session <subtype> <session_id>` | Read `session_id` back via `SessionMarkerRegex` in `OnOutputLine` to capture the UUID. |
| `assistant` | Assistant turn. `message.content` is an array of parts. | One marker line or text line per part. | `text` parts split on newlines, emitted as plain text. `tool_use` parts go through `FormatToolUse` and become marker lines. `thinking` parts are silently dropped (extended-thinking is not user-actionable; could be exposed behind a debug flag later). |
| `user` | Tool result. `message.content[].type == "tool_result"` carries the tool output. | Indented continuation `  <first-line-trimmed-to-200-chars>` under the preceding marker. | `is_error: true` marks the line as `stderr`. |
| `result` | Final-result frame at end of run. Carries `subtype` (e.g. `success`), optional `result` text, `is_error`. | `● Result (<subtype>)` or the raw `result` text. | Errors flip stream to `stderr`. |
| `rate_limit_event` | Per-turn rate-limit telemetry. Carries `rate_limit_info: { rateLimitType, status, resetsAt, overageStatus, isUsingOverage }`. | Two-part marker: `● Rate limit · <window> · <status> · reset in <human>  [window=… status=… resetsAt=… overage=… usingOverage=…]` | The bracketed kv tail is parsed back into a `ClaudeRateLimitSnapshot` for the live header pill. **Keep the kv format stable**, the regex `RateLimitMarkerRegex` reads it back. |
| (other) | Unknown frame type. | `● <type>` (catch-all). | Never leak raw JSON into the activity log — that breaks the marker classifier downstream. New frame types should get an explicit case once we know what they carry. |

### Tool-name → marker mapping

Implemented in `FormatToolUse`. The mapping is stable; new tools should land here, not in a per-tool branch in the parser.

| Claude tool | Marker line | Activity-log kind |
|---|---|---|
| `Read` | `● Read <file_path>` | `read` |
| `Write` | `● Write <file_path>` | `edit` |
| `Edit` | `● Edit <file_path>` | `edit` |
| `Glob` | `● Search glob <pattern>` | `search` |
| `Grep` | `● Search <pattern>` | `search` |
| `Bash` | `● Run <command (one-line, ≤ 200 chars)>` | `command` |
| `TodoWrite` | `● Todo update` | `todo` |
| `Task` | `● Task <description>` | `task` |
| `WebFetch` | `● Fetch <url>` | `command` |
| `WebSearch` | `● Search web <query>` | `search` |
| `NotebookEdit` | `● Edit notebook <notebook_path>` | `edit` |
| (anything else) | `● <name>` | `other` |

## Session-UUID capture

Capture is **two-step** because `OnOutputLine` runs on the *transformed* line:

1. `TransformReadLine` reads the raw `system` frame, pulls `subtype` + `session_id`, and emits `● Session <subtype> <uuid>`.
2. `OnOutputLine` runs the `SessionMarkerRegex` against that marker line, sets `info.CapturedSessionId`, and assigns `info.SessionName` to the same UUID.

The runner picks the captured UUID up in `ProjectRunner.OnCliFinishedAsync` and persists it via `_sessions.AppendSessionToChain`. Without that, every "Continue" would start a fresh session because `info.SessionName` would never advance.

**Anti-pattern:** capturing in `TransformReadLine` directly. The transform must stay a pure function over a single line; capture is a side effect on `ProcInfo` and belongs in `OnOutputLine`.

## Rate-limit pill

Anthropic streams a `rate_limit_event` frame **per turn**. We render it to a single marker line with two halves:

- A human prefix: `● Rate limit · five-hour · allowed · reset in 109 min`
- A machine kv tail: `[window=five_hour status=allowed resetsAt=1777393800 overage=allowed usingOverage=false]`

`OnOutputLine` parses the tail back into `ClaudeRateLimitSnapshot` (window, status, resetsAt, overageStatus, isUsingOverage, capturedAt). The frontend's protocol-pane header pill reads `info.LastRateLimit` via `GET /api/jobs/{id}/claude/session-info`.

If you change the marker format, you break the pill. Update both halves together and add a test that round-trips a captured frame.

## Model handling

`GetModelCatalogAsync` returns a hardcoded list (Opus 4.7, Sonnet 4.6 default, Haiku 4.5). No live discovery yet — the CLI doesn't expose a `claude models list` command at the time of writing.

`NormalizeModelId` coerces dotted forms (`claude-opus-4.7`) into the dashed form (`claude-opus-4-7`) the CLI requires. Unknown ids pass through unchanged so non-standard ids still flow.

To add a new model, append it to the list in `GetModelCatalogAsync` and add a test row in `claude-model-normalize.spec.ts` if the dotted form is realistic.

## Quirks (and what to do about them)

1. **Claude reads stdin even with `-p`.** Without an early stdin EOF, the CLI prints a 3 s "no stdin data received" warning before continuing. The base class closes `process.StandardInput` immediately after `Process.Start()`. Don't remove this.
2. **Windows npm shim quirk.** The npm-installed binary is `node_modules/@anthropic-ai/claude-code/bin/claude.exe`. An interrupted `npm i -g @anthropic-ai/claude-code` can leave a `claude.exe.old.<timestamp>` and *no* `claude.exe`, breaking `claude --version`. The fix is to reinstall: `npm i -g @anthropic-ai/claude-code`. This is documented in `supported-clis.md` and shows up as `Available=false` in the side-sheet.
3. **`thinking` blocks are dropped.** Extended-thinking content (`type: "thinking"` parts of an assistant message) is filtered out of the visible buffer. They're noisy and not user-actionable; if a debug flag becomes useful, add a config-gated branch in `TransformReadLine`.
4. **Long Bash commands get trimmed to 200 chars.** `TrimSingleLine` collapses newlines and truncates with `…`. Multi-line shell scripts won't render in full; this is intentional, the full command is in the persisted `tool_use` payload via `Read` of the JSONL.
5. **`stream-json` requires `--verbose`.** Without it, the CLI exits silently. The runner always passes both.

## Known incidents (and the fixes)

- **Sessions never resumed (Issue tracked in `TaskRunnerPlanTests`):** the captured UUID never made it back to `job.json` because the `system` frame's `session_id` was being read in `TransformReadLine` and discarded. Fix: capture in `OnOutputLine` against the transformed marker line; persist via `_sessions.AppendSessionToChain` in `OnCliFinishedAsync`.
- **Restart of finished tasks: "I'll wait for your request":** when a job in `4-review` was re-started with the same session, the runner re-issued the `RunnerFreshStart` bootstrap as a new user turn. Claude saw a duplicate of turn 1, decided the task was already done, and replied with the generic English fallback. Fix: new `runner-resume-restart.md` template + planner branch for `ManualStart + resume + initialState ∈ {Review, Completed}`. Tests in `TaskRunnerPlanTests.Start_FromReviewOrCompletedWithSession_UsesRestartPrompt`.
- **Activity log blank for 30+ s at start:** Claude's `-p` mode emits no output until the model produces its first text in the default text format. Switching to `stream-json` made every frame stream live; the synthetic `[taskboard] Started claude CLI ...` line additionally fills the gap between spawn and first frame.

## Quota probe

[`ClaudeQuotaProbe`](../../backend/Services/Quota/ClaudeQuotaProbe.cs) drives the `/usage` slash command via PTY in a scratch dir. It returns two windows: the 5-hour bucket (current session) and a weekly bucket. The probe runs in `%TEMP%/agent-taskboard-quota/claude/` so it doesn't pollute the user's `~/.claude/projects/` listing.

If `/usage` output format changes, the parser in [`ClaudeQuotaParser`](../../backend/Services/Quota/ClaudeQuotaParser.cs) is the place to update; the test fixture lives under `backend.Tests/Fixtures/quota/claude/`.

## Common tasks

### "Add a new tool marker"

1. Identify the Claude tool name (`Read`, `Edit`, `Bash`, …).
2. Add a switch arm in `FormatToolUse` that builds the marker line in the existing vocabulary (§ Marker-line vocabulary in `cli-overview`). Don't invent a new prefix.
3. Add a test under `backend.Tests` (a new `ClaudeCliServiceTests.cs` if absent — Gemini's file is the template) that constructs a synthetic `assistant` frame and asserts the emitted marker.

### "Capture new telemetry from a frame"

1. Locate the frame type in the catalogue above; add a switch arm if new.
2. Render to a marker line with a stable bracketed kv tail (`[key=value …]`) — same pattern as `rate_limit_event`.
3. Add a regex in `OnOutputLine` to read the kv tail back; assign to a typed snapshot on `ProcInfo`.
4. Expose via the existing `/api/jobs/{id}/claude/session-info` endpoint (do not introduce a new endpoint per snapshot).

### "Add a new model"

1. Append to `GetModelCatalogAsync`'s hardcoded list.
2. Test in the existing `*.spec.ts` round-trip if the user is likely to enter a dotted form.

### "Trace a 'my session didn't resume' bug"

1. Read `logs/cli-output.log` for the run that should have produced the UUID. The `● Session init <uuid>` marker line is the source.
2. Confirm `info.CapturedSessionId` was set: the next line in the runner log is `Captured Claude session id <uuid>`.
3. Confirm `OnCliFinishedAsync` ran `_sessions.AppendSessionToChain` with that id. If not, the run was cancelled or crashed before exit-monitor.
4. Confirm `RunPlanner.PlanRun` reads the persisted UUID via `info.SessionName` on the next start.

### "Add a regression test for a new behaviour"

1. Capture a real raw frame (or several) from `logs/cli-output.log` — the unparsed JSON line(s).
2. Save under `backend.Tests/Fixtures/cli/claude/<name>.jsonl` (one frame per line).
3. Add a `ClaudeCliServiceTests` that loads the fixture, runs `TransformReadLine` over each frame, and asserts the emitted marker(s).

## Fixtures

`backend.Tests/Fixtures/cli/claude/` holds raw stream-json frames captured from real runs. Format: NDJSON (one JSON object per line). New frame shapes that we observe in the wild should land here in the same PR that handles them, with a regression test that loads the fixture.
