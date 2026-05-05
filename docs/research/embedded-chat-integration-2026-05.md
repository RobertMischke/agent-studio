# Embedded Chat Integration Plan, May 2026

Status: planning. This document is the working plan for moving the v5/v7 chat-window-next-gen mockup off the standalone prototype and into the existing application surfaces. It supersedes the rollout sketch in [`docs/mockups/chat-window-next-gen/integration-plan.md`](../mockups/chat-window-next-gen/integration-plan.md) for the next set of queued slices.

## Direction

Two homes, one grammar. No new global chat window.

- **Task-scoped chat** lives in the existing task detail Activity (and Chat) surface inside the Protocol pane. Run timeline, auto-eval banner, raw Trace fallback, Verbose Debug, screenshots, commits, run-Git viewer, and the existing composer modes stay reachable.
- **Project-scoped chat** lives in the resizable orchestrator side sheet. Project picker, task tab, roadmap intake tab, attachments, and make-task action stay reachable.

`Frontend:NextGenChat` owns the conversation grammar and renderer. `Frontend:VsCodeLayout` owns the app-shell density and chrome (status bar, meta panel, activity bar). The two flags must continue to work independently, and any combination of (off/off, on/off, off/on, on/on) must render without regression. Token usage stays a first-class but compact surface in chat (small chip or drill-down link); the full token story stays in Status Bar quota, CLI Usage sheet, Workspace Token Timeline, and project token summaries.

## What has already landed (bridge)

The `chat-layout-integration-bridge` work is in. The renderer wiring is not.

| Surface | Status |
|---|---|
| `Frontend:NextGenChat` flag in `FeatureFlagsService` (default off) | Landed |
| `ConversationEvent` data contract under `frontend/src/app/components/chat/conversation-event.ts` | Landed |
| Pure `projectConversation()` projection over `parseActivityLog`, with run-aware tool-burst collapsing, watchdog and capture-fail classification, schema-drift dedupe, screenshot/commit/token aggregates, and workbench-summary/debug aggregates | Landed |
| Projection unit tests + fixtures (`conversation-projection.spec.ts`, `conversation-projection.fixtures.ts`) | Landed |
| `app-tool-burst-chip` presentational component | Landed (not yet hosted) |
| `app-verbose-debug-overlay` consumes the projection from the Protocol pane Activity tab | Landed |
| Standalone clickware prototype (`npm run mockup:chat`, `:4022`) | Landed; do not import its arrays into production |

What is **not** landed yet:

1. A host adapter that swaps the Activity tab body to render `ConversationEvent[]` when the flag is on.
2. Any production `app-conversation-view` renderer (only the Verbose Debug overlay reads the projection today).
3. A side-sheet adapter that reuses the same message components.
4. Composer toolbar consolidation (model, mode, permission, start/stop, attachments, context chips).
5. Workbench split presets in the Activity tab (Chat only / Result / Git / Preview / Debug).
6. Continuous-chat task markers in the project side sheet.
7. Playwright regression suite covering both flags off, NextGenChat on, VsCodeLayout on, both on, light/dark, mobile narrow, side-sheet wide, scenario-18 Verbose Debug, scenario-19 chat-first reading, scenario-20 embedding (no global chat window), scenario-21 light/dark parity.

## Slice order (queued tasks)

The remaining work splits into independent task-folder slices. Each slice stays small enough to ship behind the flag without regressing the off-state.

### Slice 1: `chat-conversation-event-projection` host adapter

**Goal.** With the flag on, the Activity tab renders `ConversationEvent[]` through a new `app-conversation-view` component instead of the legacy `app-activity-log-view`. Off-state unchanged.

Scope:

- New `frontend/src/app/components/chat/conversation-view.component.ts`. Inputs: `ConversationEvent[]`, `isRunning`, `variant`. Outputs: `openTrace(range)`, `openVerboseDebug()`. Renders user / agent message bubbles, `app-tool-burst-chip` for `toolBurst`, simple inline rows for `decision.orchestrator`, `supervisor.wait`, `agent.needsInput`, `system.captureFail`, `system.parserWarning`, `system.schemaDrift`, `artifact.image`, `taskMarker`, `runMarker`, `metric.token`, `traceLink`. Workbench events (`workbench.summary`, `workbench.gitPreview`, `workbench.visualPreview`, `workbench.debug`) are skipped in slice 1; the existing run timeline, summary strip, and Git viewer keep that role.
- Host adapter in `protocol-pane.component.html`: under `@if (featureFlags.nextGenChat()) { app-conversation-view ... } @else { app-activity-log-view ... }`, with the same inputs and surrounding controls (run timeline, auto-eval banner, composer) untouched.
- The adapter calls `projectConversation()` with the raw lines, run timeline, screenshots, token summary, commits, and job already in scope. No new HTTP calls.
- Trace mode stays available behind a button on `app-conversation-view` that switches the same body back to the legacy `app-activity-log-view` for the same lines.
- Side-sheet adapter is a separate ticket (slice 4); slice 1 covers task chat only.

Acceptance gate:

- All hard-preservation items in `docs/mockups/chat-window-next-gen/host-inventory.md` still work with the flag off.
- New Playwright spec `frontend/e2e/next-gen-chat-task-host.spec.ts` covers: flag off vs flag on, fixture-driven Stable evidence (tool-heavy archive, watchdog kill, schema-drift, capture-fail, image artefact, needs-input loop), light/dark theme, narrow viewport, "open Trace" button.
- `chat-window-next-gen` evidence pages in `evidence/` regenerated and gitignored as before.

Rollback: turn the flag off and the host renders exactly as today.

### Slice 2: `chat-actor-decision-cards`

**Goal.** Replace the slice-1 inline rows for `decision.orchestrator`, `supervisor.wait`, `agent.needsInput`, `system.captureFail`, `system.parserWarning`, `system.schemaDrift` with a shared `app-decision-card` component that surfaces decision, reason, evidence, action, retry budget, and token usage when expanded. Side-sheet adapter still pending.

Scope:

- `app-decision-card` lives next to `app-tool-burst-chip`. Stays presentational; host owns expand state.
- Renders the actor rail (Orchestrator / Supervisor / Agent / System) so actor identity is visible even when collapsed.
- Wires the existing `[orchestrator]` chat lane data already produced by `OrchestratorChatLog` into the decision card without changing backend code.
- Playwright cases: scenario 5 (orchestrator reissue), 6 (heuristic warning), 7 (circuit breaker), 8 (supervisor advisory), 22 (watchdog quiet/kill), 23 (needs-input loop), 24 (capture fail), 25 (duplicate sentinel/parser de-dupe), 28 (schema drift).

### Slice 3: `chat-tool-burst-collapsing` host wiring

**Goal.** Lock dense tool-burst rendering, including the v6 edge cases (failure inside burst, tests with retry/pass, artefact links) and assert collapsed-by-default.

Scope:

- `app-tool-burst-chip` already exists. This slice is mostly Playwright + small a11y polish: keyboard expand/collapse, focus ring, screen-reader label, mobile narrow layout, ensure tool-failure count is always visible without expansion.
- Spec `chat-tool-burst.spec.ts` extension with the four tool-heavy fixtures referenced by the projection tests.

### Slice 4: `chat-side-sheet-grammar-adoption`

**Goal.** Side sheet message bubbles and decision rows match the task-chat grammar.

Scope:

- `orchestrator-side-sheet.component.ts` swaps its message renderer to a side-sheet variant of `app-conversation-view`. The side sheet keeps `ChatMessage` as its source-of-truth model (no projection over `cli-output.log` here), but adapts each message into a small `ConversationEvent`-shaped record so the renderer is shared.
- Project picker, task tab, roadmap intake, attachments, make-task action are untouched. Hard preservation still applies.
- Continuous-chat task markers (scenario 16) ship in this slice as `taskMarker` rows the side sheet emits when a task lifecycle event lands.
- Playwright: side-sheet narrow + wide modes, project picker still functional, make-task still works, roadmap intake still works.

### Slice 5: `chat-composer-toolbar`

**Goal.** Move model, agent mode, permission level, start/stop, configuration, jobs, attachments, context chips into a compact composer toolbar in both hosts. Today these live in `command-deck` and the Activity tab composer; this slice consolidates without removing capabilities.

Scope:

- New `app-chat-composer-toolbar` component that wraps the existing buttons and selects.
- `command-deck.component` stays as the canonical home for "deeper" controls (project switcher, CLI type, retention rules) that live in the meta panel; the toolbar is the slim always-visible row.
- Behind `Frontend:NextGenChat`. Off-state unchanged.

### Slice 6: `chat-workbench-split-presets`

**Goal.** Add the v7 task-chat split presets (Chat only, Chat + Result, Chat + Git, Chat + Preview, Chat + Debug) as additive panes that share the Activity tab body.

Scope:

- Persists ratios in `localStorage` (`atp.flag.nextGenChat.split` keys).
- Reuses run-Git viewer, screenshot strip, run timeline, Verbose Debug overlay; does not introduce a new docking framework.
- Mobile narrow collapses the side pane; Chat stays usable.
- Playwright: scenario 30 split-pane states.

### Slice 7: `chat-window-playwright-regression-suite`

**Goal.** Lock the migration with screenshot and interaction regression coverage. Runs after slices 1 to 6.

Scope:

- One spec per scenario in `docs/mockups/chat-window-next-gen/scenarios.md`. Existing specs (`chat-tool-burst.spec.ts`, `next-gen-chat-workbench-regression.spec.ts`, `verbose-debug-overlay.spec.ts`) are extended; new specs cover scenarios 1, 2, 5-9, 10, 11, 12, 14, 16, 18-21, 22-28, 30, 31.
- Light + dark theme matrix.
- Both flag combinations.
- Image lightbox, side-sheet wide, mobile narrow.

## Risks and rollback

- **Off-state regression.** Every slice must keep the off-state exactly as today. The first review of slice 1 should boot the app with no localStorage flag and confirm the Activity tab is byte-for-byte identical (or as close as Playwright pixel-diff tolerates) to current Stable.
- **Trace fallback.** The "open Trace" button on the new renderer must reuse the legacy `app-activity-log-view` for the same lines. Deleting Trace would violate the hard rule from the README ("Do not delete the raw log").
- **Token surface drift.** The new chat must not surface tokens by replacing Status Bar quota, CLI Usage sheet, or Workspace Token Timeline. The compact `metric.token` chip is additional, not a replacement.
- **Side-sheet feature loss.** Slice 4 must not drop project picker, task tab, roadmap intake, attachments, or make-task. The side-sheet adapter wraps the existing component instead of replacing it.
- **VS Code layout interaction.** Both flags must work independently. Slice 1 specs run with VsCodeLayout off; slice 7 covers the both-flags-on combination.
- **Composer regressions.** Slice 5 cannot remove modes (Continue / Steer / Extend / New task) or controls (model, CLI type, permission). The toolbar reorders, it does not delete.

## Conflicts with ADRs

Reviewed against ADR-0001 (sequential per project), ADR-0002 (deterministic orchestration), ADR-0017 (supervisor advisory), ADR-0018 (companion app), and the newer entries.

- The chat surface presents the orchestrator as a typed actor with deterministic decisions. This **aligns** with ADR-0002. Heuristic verdicts must surface as a warning row; the projection already classifies them as `system.parserWarning` with `expectedKind: 'sentinel'`.
- Supervisor advisories appear as a distinct actor with severity. This **aligns** with ADR-0017's advice-first, force-rare model. Auto-intervention events must continue to be filtered out of the chat input loop (the bridge already guards the feedback loop on `Source: AutoIntervention`).
- The task-detail Chat surface stays sequential per project. **No conflict** with ADR-0001.
- No companion-app surface is touched. **No conflict** with ADR-0018.

No reconciliation is required at this time.

## Out of scope

Explicitly not in this plan:

- A new global chat window or chat route.
- A draggable docking framework (split presets are deterministic).
- Replacing `OrchestratorChatLog`, `RunOutcomePolicy`, or `AgentOutcomeAnalyzer`.
- Backend changes to `cli-output.log`, run timeline, or token summary endpoints.
- Removing the standalone clickware prototype (`npm run mockup:chat`).
- Replacing Status Bar quota, CLI Usage sheet, Workspace Token Timeline, or project token summaries.
- Replacing the `command-deck` component (slice 5 wraps it, does not delete it).
- Replacing per-CLI session telemetry chips on the Protocol pane header.

## Reporting

Each slice ends with:

1. Playwright spec(s) listed in this plan, run green.
2. Screenshots inline in the chat reply for the surfaces touched (light + dark, narrow + wide where applicable).
3. README and ROADMAP cross-references updated when a slice changes a documented behavior.
4. ADR entry only when a slice surfaces or changes a load-bearing decision.

## Open questions

- Slice 4: should the side-sheet adapter project on top of `ChatMessage` directly, or should we extend `ChatMessage` with a `ConversationEvent`-shaped sub-record? Decision deferred to slice 4 design step; likely extension to keep the side sheet's task-tab logic untouched.
- Slice 5: should the composer toolbar live inside `app-conversation-view` or beside it as a sibling? Probably sibling so the side sheet can reuse it without inheriting the renderer's run-aware empty state.
- Slice 7: do we need an `@billable` end-to-end pass that exercises a real CLI run through the new chat? The existing `claude-hello-world.spec.ts` covers the runner; a single billable extension that asserts the new chat shows the same evidence is enough.
