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
codex exec --json [-m <model>] "<prompt>"
```

The prompt is **the last positional argument**. `-m` selects the model. `--json` makes stdout machine-readable; without it we cannot extract the session UUID.

### Resume

```sh
codex exec resume <uuid> --json [-m <model>] "<prompt>"
```

`resume` is a **subcommand of `exec`**, taking the UUID positionally. Don't pass it as `--resume=<uuid>` (that's Copilot's flag) and don't pass it as `-r <uuid>` (that's Claude/Gemini). Codex's `exec resume <uuid>` is positional.

### Anti-patterns

- **Don't** swap argument order. `codex exec --json resume <uuid> "<prompt>"` parses `resume` as the prompt.
- **Don't** pass a non-UUID session id. `IsCompatibleSessionName` rejects non-UUIDs to keep cross-CLI session names from leaking through.

## `--json` frame model

Codex emits JSON Lines on stdout. Unlike Claude's `stream-json`, Codex frames are **pass-through**: `TransformReadLine` is the identity transform (no per-CLI translation yet — see § Limitations). `OnOutputLine` parses the raw JSON directly to capture the session UUID.

| Frame | Purpose | Action |
|---|---|---|
| `{"type":"session_meta","payload":{"id":"<uuid>",…}}` | First frame on a fresh run; carries the session UUID. | Capture `payload.id` in `OnOutputLine`, assign to `info.CapturedSessionId` and `info.SessionName`. |
| `{"type":"message",…}` etc. | Assistant text, tool use, tool results. | Pass through unchanged today. The Activity Log shows raw JSON for these. |

**Known gap:** Codex frames are surfaced raw because no `TransformReadLine` translation exists. `docs/supported-clis.md §3.2` flags this as `⚠ Pass-through; no marker-line transform yet`. When you add the transform:

1. Capture real `--json` output to `backend.Tests/Fixtures/cli/codex/` first.
2. Build the switch in `TransformReadLine` analogous to Claude's, mapping Codex tool names to the marker-line vocabulary in `cli-overview`.
3. Add a `CodexCliServiceTests` file (template: `GeminiCliServiceTests`).
4. **Don't** move session capture out of `OnOutputLine`'s raw-JSON path — that's the only place we have the full payload before the transform mangles it. Either keep `session_meta` pass-through, or move capture to read the marker line (Claude/Gemini style) — but not both.

## Session-UUID capture

```csharp
protected override void OnOutputLine(ProcInfo info, CliOutputLine line)
{
    if (info.CapturedSessionId != null) return;
    if (line.Stream != "stdout") return;
    var text = line.Text?.TrimStart();
    if (text == null || !text.StartsWith('{')) return;
    if (!text.Contains("session_meta")) return;

    using var doc = JsonDocument.Parse(text);
    if (doc.RootElement.TryGetProperty("payload", out var payload)
        && payload.TryGetProperty("id", out var id)
        && id.ValueKind == JsonValueKind.String)
    {
        info.CapturedSessionId = id.GetString();
        info.SessionName ??= info.CapturedSessionId;
    }
}
```

Note Codex captures from the **raw** JSON line because `TransformReadLine` is identity. If you ever introduce a transform that drops or rewrites `session_meta`, capture breaks.

## Model handling — live discovery

[`CodexModelDiscovery`](../../backend/Services/Cli/CodexModelDiscovery.cs) queries the CLI for its current model list and caches the result. `GetModelCatalogAsync` is a thin wrapper.

To refresh the cache, the user clicks the side-sheet refresh button which calls `/api/cli/codex/models?forceRefresh=true`.

When a CLI version bump changes the output format, the regression shows up as `Source = "live-discovery-failed"` in the catalog and the dropdown empties. Tests in `CodexModelDiscoveryTests.cs` lock the parser shape.

## Quirks (and what to do about them)

1. **Trust prompt has "1. Yes, continue" pre-selected and accepts a bare Enter.** Sending `1<Enter>` works but leaves a stray `1` in the input box that prefixes the next slash command. Use `<Enter>` alone when scripting Codex over a PTY (the quota probe does this).
2. **`/status` PTY probe is fragile.** Trust + welcome + `/status` is a chained multi-step probe; one extra prompt or layout shift breaks it. See comments in [`CodexQuotaProbe`](../../backend/Services/Quota/CodexQuotaProbe.cs). When updating, capture the new PTY transcript under `backend.Tests/Fixtures/quota/codex/` and lock with a fixture-based test.
3. **Codex reports % left, we report % used.** The probe inverts the value so the UI's `UsedPct` semantics stay consistent across CLIs. Don't double-invert.
4. **`--json` is required.** Without it, stdout is a colored panel that can't be parsed. The runner always passes it.

## Quota probe

[`CodexQuotaProbe`](../../backend/Services/Quota/CodexQuotaProbe.cs) returns two windows: a 5-hour bucket and a weekly bucket. Implementation runs `codex` over a PTY, accepts the trust prompt, navigates to `/status`, scrapes the panel.

The probe reports `% used` (1 - `% left`). Source string is `/status (PTY)`.

## Common tasks

### "Add the missing TransformReadLine translation"

Order:

1. Run a Codex job and capture `~/.runtime/cli-output/codex-*.jsonl` to `backend.Tests/Fixtures/cli/codex/`.
2. Inspect frame shapes — Codex's tool naming differs from Claude's (verify against the CLI source / observed frames).
3. Build the `TransformReadLine` switch, mapping Codex tool names to the marker-line vocabulary in `cli-overview`. Suppress noisy frames (echo of user prompt, ack frames). Surface assistant text frames as plain text.
4. Move session capture to read the marker line if the transform now rewrites `session_meta`. Otherwise leave it on the raw JSON path.
5. Add `backend.Tests/CodexCliServiceTests.cs` modelled on `GeminiCliServiceTests.cs`.

### "Codex isn't resuming"

1. Verify the persisted `sessionName` in `job.json` is a UUID.
2. Verify the `exec resume <uuid> --json "<prompt>"` argument order.
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
