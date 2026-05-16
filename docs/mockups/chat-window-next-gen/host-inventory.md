# Next-Gen Chat Host Inventory

This document is the bridge artefact for `Frontend:NextGenChat`. It lists every existing UI surface a later job will plug the shared `ConversationEvent` renderer into, and the existing surfaces those jobs are NOT allowed to remove without an equivalent replacement landing in the same task.

It complements:

- `docs/mockups/chat-window-next-gen/integration-plan.md` (rollout order and host mapping).
- `docs/mockups/chat-window-next-gen/activity-log-edge-cases.md` (event taxonomy this projection feeds).
- `frontend/src/app/components/chat/conversation-event.ts` (the data contract).
- `frontend/src/app/components/chat/conversation-projection.ts` (the pure projection scaffold).

## Hosts

There are two real chat hosts in the app today, plus a standalone clickware prototype. The next-gen renderer must land inside the real hosts. There is no new global chat window.

### 1. Task detail Activity / Chat tab

- File: `frontend/src/app/components/job-detail/protocol-pane/protocol-pane.component.ts`
- Subcomponents the projection feeds:
  - `frontend/src/app/components/activity-log-view.ts` – Conversation, Trace, Raw mode toggle.
  - `frontend/src/app/components/activity-log.parser.ts` – source of `ActivityLogGroup` and `ConversationTurn` (preserved; `conversation-projection.ts` builds on top of `parseActivityLog`).
  - `frontend/src/app/components/job-detail/protocol-pane/run-timeline.component.ts` – run boundaries and run-scoped filters.
  - `frontend/src/app/components/job-detail/protocol-pane/run-git-viewer.component.ts` – per-run Git review (lives behind the workbench Git split preset).
  - `frontend/src/app/components/job-detail/protocol-pane/protocol-image-resolver.ts` – image artefact resolution.
  - `frontend/src/app/components/job-detail/protocol-pane/watchdog-state.ts` – existing watchdog chip backing data.
- Composer surface to preserve:
  - `frontend/src/app/components/job-detail/command-deck/` – Continue, Steer, Extend, Follow-up job, attachments, context chips, access mode, CLI/model, Start, Pause.
  - `frontend/src/app/components/job-detail/prompt-pane/` – prompt history.
- Pane controls to preserve:
  - `frontend/src/app/components/job-detail/pane-toggle-bar/`.
  - `frontend/src/app/components/job-detail/layout-panes.service.ts`.
  - `frontend/src/app/components/job-detail/cli-config-card/`.
  - `frontend/src/app/components/job-detail/git-pane/` and `git-pane.service.ts`.
  - `frontend/src/app/components/job-detail/log-overlay/` – verbose technical output.

The first renderer slice may only replace the Activity tab body. Trace mode, raw access, run timeline, auto-eval banner, command deck, prompt history, and pane toggles must all keep working with the flag off and on.

### 2. Project side sheet

- File: `frontend/src/app/components/orchestrator-side-sheet/orchestrator-side-sheet.component.ts`.
- Reusable chat: `frontend/src/app/components/chat/chat.component.ts` (`app-chat`) – stays the bottom-of-host transcript renderer. The next-gen renderer wraps it; it does not delete it.
- Companion services and surfaces to preserve:
  - `frontend/src/app/components/screenshot-strip/`.
  - `frontend/src/app/components/cli-usage-sheet.ts`.
  - `frontend/src/app/components/header-quota.ts`, `quota-strip.ts`, `usage-hover-panel.ts`.
  - `frontend/src/app/components/status-bar.ts`.
  - `frontend/src/app/components/workspace-banner.ts`.
  - `frontend/src/app/components/workspace-screenshots.ts`, `workspace-token-timeline.ts`.
  - `frontend/src/app/components/token-summary-block.ts`.
  - `frontend/src/app/components/orchestrator-feed.ts`.

The side sheet keeps project picker, attachments, and the make-task action. Reading the new flag must not remove or rewire any of these surfaces in slice one.

### 3. Mockups / prototype (do not touch in production)

- Files: `frontend/src/mockups/next-gen-chat/app/` plus `frontend/src/mockups/next-gen-chat/styles.scss`.
- Standalone serve target: `npm run mockup:chat` from `frontend/`, opening `http://127.0.0.1:4022`.
- The prototype is not mounted in `App` and must not be reintroduced as a normal dev-shell overlay.
- Use the prototype as the interaction reference. Do not import its dummy data into production.

## Preserved companion data contracts

The projection feeds on existing data and must not duplicate ownership:

- `frontend/src/app/services/job.service.ts` – job, run timeline, CLI output, screenshots, commits.
- `frontend/src/app/services/git-summary.service.ts` – Git status and per-run viewer.
- `frontend/src/app/services/supervisor.service.ts` – supervisor advisories.
- `frontend/src/app/services/feature-flags.service.ts` – the new `nextGenChat` signal.
- `frontend/src/app/services/dev-tools.service.ts` – verbose debug toggles.
- `frontend/src/app/services/now-tick.service.ts` – wall clock for live status.
- `frontend/src/app/services/project-docs.service.ts`, `analysis-report.service.ts`, `drift.service.ts` – project-level evidence the side sheet exposes.

## How later jobs plug into the projection

The projection (`projectConversation`) takes a context bag and returns `ConversationEvent[]`. Subsequent jobs should follow this contract:

1. **`chat-conversation-event-projection`** wires the host adapter:
   - Task detail adapter lives next to `activity-log-view.ts`. It reads the existing `CliOutputLine[]`, `RunTimeline`, `JobInfo`, `tokenSummary`, screenshots, and commits already in the component, builds a `ConversationProjectionContext`, calls `projectConversation`, and feeds a new renderer behind the flag.
   - Side-sheet adapter lives next to `orchestrator-side-sheet.component.ts`. It reuses `ChatMessage` while the projection-driven renderer is staged behind the flag.
   - The off-state renderer is `app-activity-log-view` and `app-chat` exactly as before.

2. **`chat-tool-burst-collapsing`** styles `toolBurst` and adds the families / failures / files / artefacts strip. It does not own the data; the projection already supplies counts.

3. **`chat-actor-decision-cards`** styles `decision.orchestrator`, `agent.needsInput`, `system.captureFail`, `system.parserWarning`, and `supervisor.wait`. The projection already classifies them; this job adds the actor rail and decision card visuals.

4. **`chat-verbose-debug-view`** consumes the same projection and aggregates: actor counts, run timing, token usage by scope, image artefacts, parser warnings, schema drift. This is the read-only debug window opened from chat.

5. **`chat-window-playwright-regression-suite`** locks the migration with screenshots and interaction tests for: flag off baseline, flag on baseline, both flags on, light theme, dark theme, mobile, side sheet wide, wait-loop scenario, image lightbox, schema-drift row, workbench Git/source split, workbench compact density, chat-only preset, optional chat closed, Verbose Debug.

## Workbench split presets

The v7 task chat workbench introduces deterministic split presets. The projection emits the data the presets need; layout work lives in a later job:

| Preset | Data the projection already supplies |
|---|---|
| Chat only | Full `ConversationEvent[]` stream. |
| Result | `workbench.summary`, `metric.token`, `taskMarker`, `runMarker`. |
| Git | `workbench.gitPreview`, plus existing run-git-viewer for the in-pane editor. |
| Preview | `workbench.visualPreview`, `artifact.image`. |
| Debug | `system.parserWarning`, `decision.orchestrator`, `supervisor.wait`, `metric.token`. |

Source files and diffs belong inside Git changes, not in a standalone source browser. The projection does not invent a separate source-tree event for that reason.

## Hard preservation list (acceptance gate)

A later job is only allowed to ship the next-gen renderer when, with `Frontend:NextGenChat` off, all of the following still work exactly as today:

- Task Activity Conversation, Trace, and Raw modes.
- Live status indicator and watchdog chip.
- Run timeline and per-run Git viewer.
- Auto-eval banner.
- Composer Continue / Steer / Extend / New task / Start / Stop, attachments, mode controls.
- Reusable `app-chat`.
- Project side sheet tabs, roadmap intake, attachments, make-task.
- CLI Usage sheet.
- Status Bar quota and Workspace Token Timeline.
- Project token summaries and per-job token bubble.
- Workspace Screenshots and per-task screenshots.
- Per-run commits.
- Verbose technical output via `log-overlay`.

If any of these regress under flag off, the change must roll back and the projection adapter must be re-scoped.
