---
name: cli-gemini
description: Deep operational reference for the Google Gemini CLI driver in this project. Use when touching backend/Services/Cli/GeminiCliService.cs, the gemini stream-json frame parser (different shape than Claude!), Gemini init-frame session capture, the --skip-trust / -y flags, the buffered-stdout limitation, or GeminiQuotaProbe (identity-only). Covers tool-name vocabulary (run_shell_command, read_file, replace, glob, search_file_content, web_fetch, google_web_search), known stdout-buffering bug, and common tasks. Pair with cli-overview for cross-CLI context.
sentinel: TASKBOARD-CLI-SKILL-GEMINI-2026
---

<!-- SENTINEL: TASKBOARD-CLI-SKILL-GEMINI-2026 — pickup-tests assert any CLI driving the repo can echo this back. -->

# Google Gemini CLI (`gemini`)

Google's Gemini CLI. Distributed as the npm package `@google/gemini-cli`. Verified against v0.39.1.

> **Source:** [`backend/Services/Cli/GeminiCliService.cs`](../../backend/Services/Cli/GeminiCliService.cs) (extends `CliExecutionServiceBase`).
> **Tests:** [`backend.Tests/GeminiCliServiceTests.cs`](../../backend.Tests/GeminiCliServiceTests.cs) — most thorough driver-level test file in the project; use as a template for new CLIs.
> **Contract:** [docs/supported-clis.md §3.4](../../docs/supported-clis.md).

## Identity card

| | Value |
|---|---|
| Binary | `gemini` |
| Config key | `GeminiCli:Path` (override) |
| Version probe | `gemini --version` |
| Output mode used | `stream-json` (NDJSON, one frame per line; **different frame shape than Claude's**) |
| Session ids | UUID — strict (`IsCompatibleSessionName` rejects non-UUIDs) |
| Resume flag | `-r <uuid>` (also accepts numeric index or `latest`, but we persist UUIDs only) |
| Required headless flags | `--skip-trust` (bypass folder-trust modal), `-y` / `--yolo` (auto-approve tool calls) |
| Session storage | `~/.gemini/tmp/<project-slug>/chats/session-<timestamp>-<short>.json` (slug map in `~/.gemini/projects.json`) |
| Quota probe | Identity-only — see § Quota |

## Invocation reference

### Fresh run

```sh
gemini -p "<prompt>" -o stream-json --skip-trust -y [-m <id>]
```

`--skip-trust` is **required**. Without it the CLI blocks on a workspace-trust modal that has no headless equivalent. With it, the CLI runs untrusted but still works for non-MCP, non-extension prompts.

`-y` / `--yolo` is **required** for unattended runs. It auto-approves all tool calls (analogous to Claude's `--dangerously-skip-permissions` and Copilot's `--allow-all`). The CLI prints `YOLO mode is enabled.` to stderr — that's expected, not a bug.

`-o stream-json` selects the JSON Lines output format. Without it stdout is human-formatted text that the parser can't handle.

### Resume

```sh
gemini -p "<prompt>" -o stream-json --skip-trust -y [-m <id>] -r <uuid>
```

`-r` accepts UUID, numeric index, or the literal `latest`. We persist UUIDs only (captured from the `init` frame) so a Codex/Claude session id can never be passed in by accident.

### Anti-patterns

- **Don't** drop `--skip-trust` in headless mode. The modal blocks indefinitely.
- **Don't** drop `-y`. The default tool-approval prompt is interactive; headless mode does not surface it.
- **Don't** assume tool-call frame shape matches Claude's. Gemini uses `tool_name` + `parameters`, not `name` + `input`. Tool-name vocabulary is also distinct (see below).

## Stream-json frame catalogue

Gemini's stream-json shape differs from Claude's in important ways. Each line is a JSON object; `TransformReadLine` switches on `type`.

| `type` | Purpose | Renders to | Action |
|---|---|---|---|
| `init` | First frame; carries `session_id` and `model`. | `● Session init <session_id> (<model>)` | The UUID is read back via `SessionInitRegex` in `OnOutputLine` (the marker line, not raw JSON — the base class invokes `OnOutputLine` post-transform). |
| `message` (role `user`) | Echo of our own prompt. | (suppressed) | Never logged — no value in seeing it twice. |
| `message` (role `assistant`) | Assistant turn. `content` is a string (not an array of parts like Claude). | One line per `\n`-split segment of `content`. | Plain text. |
| `tool_call` / `tool_use` | Tool invocation. Shape: `{tool_name, tool_id, parameters: {…}}`. | A marker line via `FormatToolUse` mapping the Gemini tool name to the marker vocabulary. | See tool-name table below. |
| `tool_result` (status `success`) | Success ack — no payload to surface. | (suppressed) | Errors come through stderr ("Error executing tool ...") so we don't duplicate. |
| `tool_result` (status `error` / other) | Failure status. | `  tool_result: <status>` | Continuation line under the preceding tool call. |
| `result` | Final-result frame. Optionally carries `stats: { duration_ms, total_tokens, … }`. | `● Result <status> (<tokens> tokens, <duration_ms>ms)` or `● Result <status>` | Errors flip stream to `stderr`. |
| (other) | Unknown frame. | Pass-through. | New types should get a case after capturing a fixture. |

### Tool-name → marker mapping

Verified against `@google/gemini-cli` v0.39.1's `ToolRegistry` built-ins. **Two name shapes per row** because newer versions use snake_case, older versions used PascalCase.

| Gemini tool | Marker line | Activity-log kind |
|---|---|---|
| `read_file` / `ReadFile` | `● Read <absolute_path \| path>` | `read` |
| `write_file` / `WriteFile` | `● Write <absolute_path \| path>` | `edit` |
| `edit` / `Edit` / `replace` | `● Edit <file_path \| path>` | `edit` |
| `glob` / `Glob` | `● Search glob <pattern>` | `search` |
| `search_file_content` / `Grep` | `● Search <pattern>` | `search` |
| `run_shell_command` / `Shell` / `Bash` | `● Run <command (one-line, ≤ 200 chars)>` | `command` |
| `web_fetch` / `WebFetch` | `● Fetch <url>` | `command` |
| `google_web_search` / `WebSearch` | `● Search web <query>` | `search` |
| (anything else) | `● <name>` | `other` |

## Session-UUID capture

The `init` frame is rendered to a marker line by `TransformReadLine`. `OnOutputLine` runs on the **transformed** line and reads the UUID via regex:

```csharp
private static readonly Regex SessionInitRegex = new(
    @"●\s*Session init\s+(?<uuid>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-…)",
    RegexOptions.Compiled);
```

This is the same indirect-capture pattern as Claude. **Do not** try to read the raw JSON in `OnOutputLine`; by the time it runs, the JSON has been replaced by the marker line.

## Quirks (and what to do about them)

1. **`Warning: True color (24-bit) support not detected.`** prints on **stderr**. So does `YOLO mode is enabled.`. `TransformReadLine` lets stderr pass through unchanged so these surface as separate lines (Activity Log shows them as ERR, harmless).
2. **`-y` is mandatory for unattended runs.** Without it, the tool-approval prompt blocks. The CLI prints the YOLO confirmation to stderr each run; that's how you know it's active.
3. **`--resume` accepts non-UUID values, but we don't persist them.** `latest` and numeric indexes are valid CLI arguments but we never write them to `job.json` — we always have the captured UUID instead. `IsCompatibleSessionName` rejects them so they can't sneak in via cross-CLI session sharing.
4. **`tool_use` frames use `tool_name` + `parameters`, not `name` + `input`.** Don't copy Claude's `FormatToolUse` blindly. The Gemini service has a parameter-fallback chain (`parameters` → `input` → `args`) for forward-compatibility but the canonical shape is `parameters`.
5. **The `init` frame's session UUID is captured indirectly.** Capturing it raw in `OnOutputLine` would not work — see § Session-UUID capture.
6. **The `result` frame carries useful stats.** `total_tokens` + `duration_ms` are surfaced; ignoring them would make the run feel opaque. If new fields appear (`tool_calls`, etc.), add them to the marker.

## Known limitation: stdout buffering when spawned with redirected stdout

When the runner spawns `gemini` via `Process.Start` with `RedirectStandardOutput=true`, the CLI emits the `init` stream-json frame promptly but **buffers the remaining `message` / `tool_use` / `tool_result` / `result` frames** for the lifetime of the run, and the buffer is dropped on exit. The same invocation from a regular shell flushes correctly.

Symptoms:
- Job completes with `exitCode=0`, sometimes in seconds, sometimes after a long pause.
- Activity Log shows only `● Session init <uuid> (<model>)` plus the stderr warnings.
- The captured session UUID is correct and persisted, so resume works.
- The on-disk `cli-output.log` is missing the same frames — it's a CLI-side flush issue, not a runner-side parser bug.

Fixing this requires either a PTY-based spawn (analogous to the quota probe path) or a tiny Node wrapper that does line-buffered passthrough. Tracked in [docs/supported-clis.md §3.4 "Known limitation"](../../docs/supported-clis.md). **When this lands, do not silently switch the spawn path** — it changes the semantics of `Process.Stop` (PTY kills behave differently) and the behaviour of the orphan reaper. Add a config flag, default off, validate the new path under the e2e billable spec first.

## Model handling

`GetModelCatalogAsync` returns a hardcoded list (Auto default, 2.5 Pro, 2.5 Flash, 2.5 Flash-Lite, 3 Flash Preview). The bundle ships a static model registry; no live `gemini models list` exists.

The `auto-gemini-3` "Auto" tier transparently picks per request and reports per-model usage in the result frame. That's why the result frame's `stats` block sometimes shows model breakdowns we didn't pick.

To add a new model, append to the list in `GetModelCatalogAsync`. No version-keying like Claude's hardcoded-list-with-version pattern — Gemini's CLI has been stable enough that we can keep the list flat.

## Quota probe — identity-only

[`GeminiQuotaProbe`](../../backend/Services/Quota/GeminiQuotaProbe.cs) reads `~/.gemini/google_accounts.json` + `~/.gemini/settings.json` to surface identity (email + auth type) and runs a tiny headless ping to capture the default model. **Daily limit / reset time is deferred.**

Why identity-only:
- Quota numbers (daily limit, remaining, reset time) are fetched dynamically via `refreshAvailableCredits()` against an authenticated Google endpoint and only rendered in the interactive `/stats model` panel. There is **no headless mode** for it.
- PTY scraping the interactive panel is feasible but requires the workspace to be trusted *and* OAuth tokens to be hot — non-trivial in a scratch dir.
- Deferred until user demand justifies the cost.

The probe currently surfaces an explanatory `Error` field so the UI doesn't claim numbers we don't have. **Keep it that way** until you have the panel-scraping working end-to-end; an empty quota panel is better than a misleading one.

## Common tasks

### "Add a regression test for a new frame"

1. Capture the raw JSON line from `~/.runtime/cli-output/gemini-*.jsonl` or run `gemini -p "<prompt>" -o stream-json --skip-trust -y` directly and pipe to a file.
2. Save to `backend.Tests/Fixtures/cli/gemini/<name>.jsonl`.
3. Add a `GeminiCliServiceTests` test that loads the fixture, runs `TransformReadLine`, asserts the marker output. Use the existing tests in that file as templates.

### "Add a new tool to the marker mapping"

1. Confirm both name shapes (snake_case + PascalCase) by running the CLI version that exposes the tool.
2. Add a row to `FormatToolUse`'s `switch` block.
3. Add a `TransformReadLine_<Tool>MapsToMarkerLine_RealFrameShape` test with a fixture frame.

### "The buffered-stdout bug bit me"

If you see only the `● Session init` line and the run completed cleanly, you hit the limitation in § Known limitation. There is no in-driver workaround. Workarounds at the orchestration level: run the same prompt via a PTY-spawned `gemini` (template: the quota probe path), or wrap with a small Node script that does line-buffered passthrough.

### "Add quota panel scraping (close the open quota gap)"

1. Stand up a trusted scratch dir under `%TEMP%/agent-taskboard-quota/gemini/<run-id>/`.
2. Drive `gemini` over a PTY, navigate to `/stats model`, scrape the panel.
3. Map percentages + reset times to `QuotaWindow` records.
4. Lock with a captured PTY transcript fixture under `backend.Tests/Fixtures/quota/gemini/`.

## Fixtures

`backend.Tests/Fixtures/cli/gemini/` holds raw stream-json frames. The Gemini test file is the model: one test per frame shape, with the raw JSON inlined as a triple-quoted string. New frame shapes go here in the same PR that handles them.
