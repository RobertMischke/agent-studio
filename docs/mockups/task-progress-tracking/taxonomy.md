# Task-Progress-Tracking Taxonomy

The vocabulary the implementation must use. If a code change introduces a new term, update this file in the same commit.

## Entities

**Plan.** The agent's ordered list of top-level intentions for the current run, as the agent itself stated them. There is at most one active plan per job. A plan is materialised as a sequence of *snapshots*; the latest snapshot is the current plan.

**Snapshot.** One observation of the plan at a single point in time, sourced from a single CLI frame. Immutable, append-only, ordered by `seq`. A snapshot has:

- `ts` - frame timestamp.
- `seq` - monotonic per job.
- `source` - the CLI frame kind that produced it: `claude/TodoWrite`, `codex/update_plan`, or `heuristic/numbered-list` (fallback).
- `items` - the ordered list of plan items as the agent reported them.

**Plan item.** One entry inside a snapshot. Has:

- `id` - stable identifier across snapshots within the same plan. Derived from the agent's own id when present (Claude TodoWrite has stable ids); otherwise hash of normalised title plus position.
- `title` - human-facing label. The Claude `content` field, the Codex `step` field, or the heuristic-extracted line.
- `status` - one of `pending`, `active`, `done`. (We normalise `in_progress` -> `active` and `completed` -> `done` at ingest.)

**Sub-action.** One tool call observed by the runtime that we attribute to a specific plan item. Derived, never persisted. Has:

- `ts` - tool-call start time.
- `tool` - tool name (`Read`, `Edit`, `Bash`, ...). Same vocabulary the activity log uses.
- `label` - one-line human-facing summary; tool-specific (`Edit foo.cs`, `Run npm test`, `Search "TodoWrite"`).
- `itemId` - the plan-item this sub-action belongs to.

## State grammar

Plan items move only forward: `pending -> active -> done`. The agent occasionally reorders or rewrites items between snapshots; we treat that as a normal sequence of new snapshots and never edit a prior snapshot. If the agent removes an item, it disappears from the latest snapshot but its sub-actions remain attributed to it via `id` (so a later "show all sub-actions" view stays correct).

A plan can have **at most one** `active` item at a time. If a snapshot violates that rule (Claude has been observed marking two items in_progress in one frame), the parser keeps the first and downgrades the rest to `pending` with a warning.

## Derivation rules

**Sub-action attribution.** Walk `tool-calls.jsonl` and `plan-snapshots.jsonl` in merged timestamp order. Maintain a single variable `currentItemId` initialised to `null`. On each event:

- Plan snapshot: set `currentItemId` to the id of the (single) `active` item in this snapshot. If none is active, leave it unchanged.
- Tool call: emit a sub-action with `itemId = currentItemId`. If `currentItemId` is `null`, the sub-action is attributed to a synthetic `unassigned` bucket (rendered as "before plan").

This is a pure function over the two files. It is replayed on every read; no cache is required. The job's `plan-snapshots.jsonl` is small (one line per `TodoWrite` call, typically tens of lines per run), and `tool-calls.jsonl` is already loaded for other views.

**Soft-estimate median.** For the active item, compute the median number of sub-actions across the items in the same plan that have already reached `done`. If fewer than two items are done, no median is shown (we do not show estimates from a single sample). The median is the position of the *soft estimate band* in the live ticker.

## Progress shape (the four cues)

The active item carries four UI signals, all derived from telemetry, all updateable at SignalR cadence (no extra polling):

1. **Live ticker.** A horizontal row of one mark per sub-action observed since the item activated. Mark colour follows tool kind (read = subtext, edit = peach, run = sky, search = mauve). No fixed width; wraps after N marks per row.
2. **Latest sub-action label.** The last tool's `label`, rendered in monospace under the ticker. Truncated to one line.
3. **Soft estimate band.** Translucent vertical mark drawn at position `median(sub-actions of completed siblings)` in the live ticker row. Hidden when fewer than two siblings are done.
4. **Heartbeat dot.** A pulsing dot next to the active item. Pulses for ~2 s after each tool call; fades after 30 s of silence.

Combined, the four answer "where is the agent" without a denominator. The implementation must ship all four together; shipping only the ticker without the soft band makes the surface feel like progress-bar theatre.

## Action vocabulary (UI controls)

The strip exposes a small action surface:

| Action | Effect |
|--------|--------|
| Click on `done` item | Expand to show its full sub-action list, verbatim. |
| Click on `active` item | Scroll the activity log to the moment the item activated. |
| Click on `pending` item | No-op (item has not run yet). |
| Click on heading | Collapse the strip to a one-line summary; click again to expand. |

No action ever moves a job, edits state, or talks back to the CLI. The strip is read-only.

## CLI-honest fallback

When the active CLI is Claude or Codex, the strip uses native frames and shows no badge. When the CLI is Copilot or Gemini, the implementation must:

1. Run a heuristic numbered-list extractor over the agent's prose lines (e.g. lines that match `^\s*\d+\.\s+`) and treat them as plan items.
2. Render the strip with a clearly visible `heuristic plan` badge in yellow.
3. Disable the soft-estimate band (heuristic timing is too noisy to median-compare).

If the heuristic finds no plan, hide the strip entirely. We do not show an empty placeholder; the activity log on its own is the correct surface in that case.

## What this taxonomy deliberately does not include

- **No agent-driven re-plan.** The orchestrator never asks the model to summarise or re-derive the plan. The plan is whatever the agent itself emitted.
- **No cross-job aggregation.** The strip is per-job. A multi-job dashboard can build on top later, but it is out of scope for this surface.
- **No editing.** The user cannot mark an item done, reorder items, or add an item. The plan reflects the agent's view; the orchestrator does not pretend to share the pen.
- **No dependency on a known total.** The progress shape is explicitly not a "k of N" bar; we never claim a denominator we do not have.

If a future request would add any of these, surface the conflict before implementing.
