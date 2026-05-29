# CLI + Model Selector Audit

Snapshot of every place in `frontend/src/` where the user picks a CLI (`copilot`
/ `claude` / `codex` / `gemini`) and/or a model. Captured to scope the
"homogenise the CLI + model selector" task: produce one reusable component and
delete the bespoke widgets each surface owns today.

The CLI vocabulary is fixed by `frontend/src/app/models/task.model.ts`:

```ts
export type CliType = 'copilot' | 'claude' | 'codex' | 'gemini';
export const CLI_TYPES: CliType[] = ['copilot', 'claude', 'codex', 'gemini'];
```

Per-CLI model catalogs come from `CliCatalogStore`
(`frontend/src/app/services/cli-catalog.store.ts`), hydrated at app boot via
`hydrateAll()` (ADR-0046). Every surface below should consume that store
instead of re-querying or hard-coding model lists.

## Sites in scope (migrate to the unified selector)

### 1. Chat composer in the protocol pane — `<app-chat-model-badge>`
- **Component:** `frontend/src/app/features/job-detail/components/chat-model-badge/chat-model-badge.component.ts:53`
- **Template:** `frontend/src/app/features/job-detail/components/chat-model-badge/chat-model-badge.component.html:1`
- **Used by:** protocol-pane chat-compose footer
  (`frontend/src/app/features/job-detail/components/protocol-pane/protocol-pane/protocol-pane.component.html:435`)
  and the overview tab's Agent block
  (`frontend/src/app/features/job-detail/components/prompt-pane/overview-pane/overview-pane.component.html:92`).
- **Shape:** chip trigger ("✴️ opus 4.7 ▾") that opens a custom popover with
  one row of CLI pills + a column of model pills + Cancel / Done. CLI switch
  re-loads the model list inside the open popover; clicking a model without
  changing CLI auto-commits.
- **CLI list filtered?** No — iterates `CLI_TYPES`.
- **Model list CLI-aware?** Yes — reads from `CliCatalogStore.ensure(cli)` on
  pill click.
- **Writes:** emits `commit = { cliType, model }`; parent (`JobDetailComponent.onAgentConfigCommit`,
  `frontend/src/app/features/job-detail/task-detail.ts:825`) PUTs cli-type + model in
  sequence with optimistic UI.

### 2. Code-Review panel — flat `<select>` (the user's call-out)
- **Template:** `frontend/src/app/features/job-detail/components/protocol-pane/code-review-panel/code-review-panel.component.html:5`
- **Controller:** `frontend/src/app/features/job-detail/components/protocol-pane/code-review-panel/code-review-panel.component.ts:45`
- **Shape:** Bootstrap-style `<select>` with three hard-coded Claude entries
  (`Opus 4.7 (default)`, `Sonnet 4.6`, `Haiku 4.5`) next to a "▶ Run Code
  Review" button. This is the one the user flagged as "doesn't match the
  typical look-and-feel".
- **CLI list filtered?** Yes — Claude-only, no picker.
- **Model list CLI-aware?** No — hard-coded to a curated Claude list, not
  read from `CliCatalogStore`.
- **Writes:** `runCodeReview(jobId, { model: selectedModel() }, watchPath)` →
  `POST /api/jobs/{id}/code-review`. The backend already accepts `cliType`
  too (`backend/Endpoints/Jobs/JobCodeReviewEndpoints.cs:162`); the frontend
  just never sends it.
- **Migration note:** the backend has accepted both fields since the endpoint
  shipped, so wiring up a full CLI+model picker is purely a frontend change.

### 3. Command-deck (job-detail toolbar)
- **Template:** `frontend/src/app/features/job-detail/components/command-deck/command-deck.component.html:42`
- **Controller:** `frontend/src/app/features/job-detail/components/command-deck/command-deck.component.ts:25`
- **Shape:** a tab-strip of CLI pills (one per `CLI_TYPES` entry) plus a flat
  `<select>` for model. Distinct visual treatment from the chat badge.
- **CLI list filtered?** No.
- **Model list CLI-aware?** Yes — parent owner (`task-detail.ts` →
  `loadModelCatalog`) re-fetches via `CliCatalogStore` on CLI change.
- **Writes:** `(cliTypeChange)` and `(modelChange)` events; parent owns the
  PUT calls.

### 4. Create-task dialog (board)
- **Template:** `frontend/src/app/features/board/components/create-task-dialog/create-task-dialog.component.html:94`
  (CLI buttons) and `:110` (model `<select>`).
- **Controller:** `frontend/src/app/features/board/components/create-task-dialog/create-task-dialog.component.ts:63`
- **Owning service:** `frontend/src/app/features/board/state/create-task-form.service.ts`
- **Shape:** "Agent" row of CLI pills + "Model" row with a flat `<select>`,
  visually different again from the command-deck and the chat-model-badge.
- **CLI list filtered?** No.
- **Model list CLI-aware?** Yes — service owner re-fetches via
  `CliCatalogStore` on CLI change.
- **Writes:** mutates form fields `newCliType`, `newModel`; sent on submit
  via `createJob({ cliType, model })`.

### 5. Status-bar default-CLI + default-model pickers
- **Template:** `frontend/src/app/features/shell/components/status-bar/status-bar.html:42`
  (default CLI) and `:65` (default model).
- **Controller:** `frontend/src/app/features/shell/components/status-bar/status-bar.ts:36`
- **Menu builders:** `frontend/src/app/features/shell/components/status-bar/status-bar-menu-builders.ts`
- **Shape:** two separate status-bar button-triggers, each opening
  `<app-menu>` popups (one for CLI, one for model). Two pickers visually,
  conceptually one default. Models served through `CliCatalogStore` with an
  explicit "Refresh catalog" row.
- **CLI list filtered?** No.
- **Model list CLI-aware?** Yes — re-loads on CLI selection.
- **Writes:** emits `defaultCliChange` and `defaultModelChange` to the shell,
  which persist via `ClientDefaultsService`. Listened to by the create-task
  form (`create-task-form.service.ts:148`).

## Sites that look related but stay out of scope

### 6. Project-detail "Orchestrator model" `<select>`
- **Template:** `frontend/src/app/features/project-detail/components/project-detail/project-detail.html:178`
- **Hardcoded list:** `frontend/src/app/features/project-detail/components/project-detail/project-detail.models.ts:9`
- **Why excluded:** this is not a CLI/model choice for a task agent. The
  orchestrator process always runs on Claude and the list is intentionally
  narrow (Default Opus 4.7 / Opus 4.7 / Sonnet 4.6 — Haiku and non-Claude
  CLIs are deliberately excluded for capability reasons documented in the
  same file). The unified selector is for task-agent CLI+model choice; the
  orchestrator-model picker is a different concept and stays as-is.
- **Verdict:** leave the existing `<select>` in place.

### 7. CLI-config card — `<app-cli-config-card>`
- **File:** `frontend/src/app/features/job-detail/components/cli-config-card/cli-config-card.component.ts:17`
- **Why excluded:** configures *how* a CLI is reachable on the box (path,
  GitHub token). It does not pick a CLI for a task.

### 8. CLI usage / admin sheets — `CliUsageSheetComponent`, `CliAdminPanelComponent`, `CliSessionsPanelComponent`
- **Why excluded:** read-only inventory and admin surfaces. They display the
  installed CLIs and current sessions, they do not let the user pick one for
  task execution.

## Cross-cutting helpers worth keeping

- `frontend/src/app/services/format.util.ts` — `cliTypeLabel`,
  `cliTypeIcon`, `shortModelName`, `formatMultiplier`. The unified selector
  should reuse these instead of duplicating the per-CLI label/icon switch.
- `frontend/src/app/services/cli-catalog.store.ts` — single source of truth
  for per-CLI model catalogs. The unified selector must read through this
  store; new sites must not re-issue `GET /api/cli/{cliType}/models` on
  their own.

## Summary

| # | Site | Shape today | CLI filter | Model CLI-aware |
|---|------|-------------|------------|-----------------|
| 1 | Chat composer / Overview Agent row | chip trigger → custom popover | none | yes |
| 2 | Code-Review panel | flat `<select>` (Claude-only hard-coded) | **Claude-only** | **no** |
| 3 | Command-deck (job-detail) | CLI pill strip + flat `<select>` | none | yes |
| 4 | Create-task dialog | CLI pill strip + flat `<select>` | none | yes |
| 5 | Status-bar default pickers | two separate `<app-menu>` triggers | none | yes |
| 6 | Project orchestrator model | flat `<select>` (Claude-only, narrow) | **Claude-only, intentional** | — |
| 7 | CLI-config card | not a selector | — | — |
| 8 | CLI usage / admin sheets | read-only inventory | — | — |

Sites 1–5 collapse into a single `<app-cli-model-selector>` chip control whose
default presentation mirrors the existing chat-model-badge (`CLI · model ▾`
trigger → CLI pills + model pills inside one popover). Site 2 specifically
loses its Claude-only restriction; the backend already accepts arbitrary
`cliType` / `model` values for code-review. Sites 6–8 stay as they are.
