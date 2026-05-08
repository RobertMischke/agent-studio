# Task Progress Tracking - Mockup

Design exploration. **A click-dummy plus a taxonomy.** Goal: settle the surface for showing a CLI's internal task plan above the activity log, before any code lands.

This folder is the spec the implementation refers back to. It documents behaviour the project is about to grow on top of an observability stream that already exists in production.

## What this is

A meta-level strip above the per-job activity log that surfaces the CLI's own internal task plan: the list of things the agent told itself it was going to do. Each top-level item gets a title, a status, and (once finished) the sub-actions it expanded into. The currently active item gets a live progress shape that does not require a known total.

The strip is built by **passive parsing of the run's existing telemetry**. It does not call the model again, does not run a second LLM, and does not re-read the agent's prose. The data is already on disk; this feature reads it and renders it.

## Why this is its own surface, not a free-text drilldown

The activity log is a chronological stream of every line the CLI emitted. It is precise but flat: the user sees `Read X`, `Edit Y`, `Run Z` in order, and has to mentally fold them back into the higher-level intentions the agent stated up front ("I'll do five things: A, B, C, D, E"). That fold is exactly what the user is asking the orchestrator to do for them.

The plan strip and the activity log are **two views of the same data**:

- The plan strip groups tool calls under the plan item that was active when they fired.
- The activity log keeps the linear chronology.

Both stay live; both stay scrollable; the strip just answers the questions the log cannot answer at a glance: *which of the five planned things is the agent on right now, how far in is it, and what did the others actually consist of when they finished.*

## Where the data comes from (per CLI)

This is intentionally CLI-specific because each CLI emits planning information differently.

| CLI | Source frame | Shape | Tracked? |
|-----|--------------|-------|----------|
| Claude Code | `tool_use` with `name=TodoWrite` | `input.todos: [{content, activeForm, status: pending\|in_progress\|completed}]` | Yes - native |
| Codex CLI | `tool_use` with `name=update_plan` | `input.plan: [{step, status: pending\|in_progress\|completed}]` | Yes - native |
| GitHub Copilot | (none structured) | n/a | Heuristic fallback or "no plan tracked" badge |
| Gemini CLI | (none structured) | n/a | Heuristic fallback or "no plan tracked" badge |

The Claude and Codex frames are **already streamed** through the existing CLI drivers (see `backend/Services/Cli/ClaudeCliService.cs` `FormatToolUse` for the Claude path; the Codex driver does the equivalent). The raw frames are already persisted to `<job>/logs/tool-calls.jsonl` for diagnostics. Nothing about the runtime CLI integration changes.

The fallback for Copilot and Gemini is a heuristic numbered-list extractor over the agent's prose - low fidelity, but better than nothing. It must be clearly badged in the UI as `heuristic plan` so the user does not trust it the same way as the native frames.

## Persistence

A new per-job file: `<job>/logs/plan-snapshots.jsonl`. One line per snapshot, append-only.

```jsonl
{"ts":"2026-05-08T06:21:32Z","seq":1,"source":"claude/TodoWrite","items":[{"id":"abc","title":"Inspect repo","status":"pending"},{"id":"def","title":"Draft README","status":"pending"}]}
{"ts":"2026-05-08T06:21:38Z","seq":2,"source":"claude/TodoWrite","items":[{"id":"abc","title":"Inspect repo","status":"in_progress"},{"id":"def","title":"Draft README","status":"pending"}]}
{"ts":"2026-05-08T06:23:11Z","seq":3,"source":"claude/TodoWrite","items":[{"id":"abc","title":"Inspect repo","status":"completed"},{"id":"def","title":"Draft README","status":"in_progress"}]}
```

Sub-actions are **derived at read time** by walking `tool-calls.jsonl` between snapshot N and snapshot N+1 and attributing each tool call to whichever item was `in_progress` at that moment. We do not persist the attribution; replaying from the two existing JSONL files is cheap and keeps the source of truth single.

## API

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/api/jobs/{jobId}/plan?watchPath=...` | Returns `{currentSnapshot, history, subActionsByItemId, source}`. |

Live updates ride the existing SignalR hub: when a new line is appended to `plan-snapshots.jsonl`, the runtime publishes a `plan-updated` event so the frontend re-fetches.

## UI: the four progress cues

The user's "geiler Progress-Indikator" requirement is the load-bearing UI claim. The challenge is that the agent does not know how long an item takes when it starts - so we cannot show a bar that fills toward a known target. Instead we layer four cues, all derivable from telemetry without an LLM:

1. **Live tool-call ticker.** A row of small marks under the active item, one per tool call observed since the item flipped to `in_progress`. Visible accumulation of work. Resets when the next item activates.
2. **Latest sub-action label.** The most recent tool's verb-plus-arg, truncated. ("Editing TaskRunnerService.cs"). Updates live, in monospace.
3. **Soft estimate band.** Translucent reference mark drawn at the median sub-action count of the *already-completed* items in the same plan. When the live ticker passes that mark, the user immediately sees "this one is taking longer than its siblings did."
4. **Heartbeat pulse.** A faint dot whose pulse fades out after 30 s without a new tool call. Tells the user the difference between "actively working" and "sitting in extended thinking" or "stuck."

Combined, the four answer "where roughly is the agent?" without needing a known denominator.

For **completed items**, the same row of tool-call marks is preserved (compressed); clicking expands a verbatim list of the sub-actions that occurred during it. That is the "Sub-Tasks, die da entstanden sind" requirement.

For **pending items**, just the title and a dim circle.

## Hard boundaries (read before extending)

- **Read-only observability.** This feature never writes to `job.json`, never moves a folder, and never spawns a second CLI. It is parsing and rendering, full stop.
- **No LLM calls.** Token cost is a deliberate non-goal. If a future enhancement would require asking a model "what are these tool calls about?" - that is a different feature and a different ADR.
- **CLI-honest.** When a CLI does not emit structured plan frames, the UI badges the strip as `heuristic plan` (Copilot, Gemini) or hides it entirely. We do not synthesize a plan that pretends to be native.
- **Off the critical path.** The plan strip is purely additive UI; if parsing fails, the activity log keeps working as before.

If a future request would relax any of those, surface the conflict before implementing.

## Files

- [taxonomy.md](taxonomy.md) - vocabulary (snapshot, plan-item, sub-action, progress-shape), state grammar, derivation rules.
- [ui.html](ui.html) - clickable dummy. Open in a browser. Catppuccin-ish dark to match the real frontend. Includes a CLI-variant switcher (Claude / Codex / Copilot-fallback) and a Play button that simulates a fresh run end-to-end.

## Implementation slice (when this lands)

1. Backend: `PlanSnapshotWriter` hooked off the existing `tool_use` parsing in `ClaudeCliService` and the equivalent in the Codex driver. Writes one line per `TodoWrite` / `update_plan`.
2. Backend: `PlanReader` derives sub-action attribution by replaying `tool-calls.jsonl` against `plan-snapshots.jsonl`. Pure function, unit-tested.
3. Backend: `GET /api/jobs/{id}/plan` endpoint + SignalR `plan-updated` event.
4. Frontend: `<plan-strip>` standalone component above the activity log. Reads from a new signal store fed by SignalR.
5. Frontend Playwright: spec that loads a fixture `plan-snapshots.jsonl` + `tool-calls.jsonl`, asserts the four progress cues render, captures screenshots of pending / active / completed states.
6. Copilot / Gemini fallback: heuristic numbered-list extractor. Behind a feature flag; ships only after the native paths are stable.

The slice is intentionally separable: 1-3 are useful even if 4 is not yet wired (an external tool can hit the API). 4 is useful even if 6 never ships (most boards run Claude or Codex).
