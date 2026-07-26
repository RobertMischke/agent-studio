# CLI + Model Selector Audit

Snapshot of every place in `frontend/src/` where the user picks a task-agent CLI
(`copilot` / `claude` / `codex` / `gemini`) and/or a task-agent model.

The shared control is `frontend/src/app/components/cli-model-selector/`. It
renders one chip-style trigger and one popover containing every CLI from
`CLI_TYPES`, the CLI-aware model catalog from `CliCatalogStore`, optional
thinking levels, and stable `data-testid` values. No task-agent call site hides
a CLI; defaults are expressed by input values, not by filtering the list.

## Unified Selector Sites

### 1. Code Review Panel

- **File:** `frontend/src/app/features/task-detail/components/protocol-pane/code-review-panel/code-review-panel.component.html:5`
- **Shape:** `app-cli-model-selector` chip next to `Run Code Review`.
- **CLI list filtered?** No. The shared picker iterates all `CLI_TYPES`.
- **Model list CLI-aware?** Yes. The shared picker reads `CliCatalogStore` and reloads when the CLI changes.
- **Writes:** `(commit)` calls `CodeReviewPanelComponent.onAgentCommit`, which updates `selectedCli`, `selectedModel`, `selectedThinkingLevel`, and local storage. `runReview()` posts `{ cliType, model, thinkingLevel }`.

### 2. Protocol Chat Composer

- **File:** `frontend/src/app/features/task-detail/components/protocol-pane/protocol-pane/protocol-pane.component.html:497`
- **Shape:** `app-cli-model-selector` chip in the composer action row.
- **CLI list filtered?** No.
- **Model list CLI-aware?** Yes.
- **Writes:** `(commit)` emits `agentConfigCommit` to `JobDetailComponent.onAgentConfigCommit`, which persists the task CLI/model/thinking override through the existing task update endpoints.

### 3. Overview Agent Row

- **File:** `frontend/src/app/features/task-detail/components/prompt-pane/overview-pane/overview-pane.component.html:104`
- **Shape:** same `app-cli-model-selector` chip as the protocol composer.
- **CLI list filtered?** No.
- **Model list CLI-aware?** Yes.
- **Writes:** `(commit)` emits `agentConfigCommit` through `PromptPaneComponent` to `JobDetailComponent.onAgentConfigCommit`.

### 4. Command Deck

- **File:** `frontend/src/app/features/task-detail/components/command-deck/command-deck.component.html:46`
- **Shape:** `app-cli-model-selector` chip in the start/stop command bar; a disabled copy appears in the collapsed running state at line 10.
- **CLI list filtered?** No.
- **Model list CLI-aware?** Yes.
- **Writes:** split `(cliTypeChange)`, `(modelChange)`, and `(thinkingLevelChange)` events for the parent `JobDetailComponent` draft state.

### 5. Create Task Dialog

- **File:** `frontend/src/app/features/board/components/create-task-dialog/create-task-dialog.component.html:94`
- **Shape:** `app-cli-model-selector` chip in the Agent field.
- **CLI list filtered?** No.
- **Model list CLI-aware?** Yes.
- **Writes:** `(cliTypeChange)` emits to `CreateTaskFormService`; `(modelChange)` and `(thinkingLevelChange)` update the dialog draft signals used on submit.

### 6. Status Bar Workspace Defaults

- **File:** `frontend/src/app/features/shell/components/status-bar/status-bar.html:45`
- **Shape:** `app-cli-model-selector` chip in the status bar.
- **CLI list filtered?** No.
- **Model list CLI-aware?** Yes.
- **Writes:** `(commit)` calls `StatusBarComponent.onDefaultCommit`, persists local storage and `ClientDefaultsService`, then emits default changes to the shell/create-task form.

### 7. Project Settings Inherited Default

- **File:** `frontend/src/app/features/project-detail/components/project-settings-panel/project-settings-panel.component.html:34`
- **Shape:** disabled `app-cli-model-selector` chip showing the inherited workspace default.
- **CLI list filtered?** No, but disabled because this card is read-only.
- **Model list CLI-aware?** Yes when opened elsewhere; this instance does not open because it is disabled.
- **Writes:** none. The card links to Workspace settings for edits.

### 8. Project Onboarding Default Agent

- **File:** `frontend/src/app/features/shell/components/onboard-project-dialog/onboard-project-dialog.component.html:38`
- **Shape:** `app-cli-model-selector` chip in the new-project dialog.
- **CLI list filtered?** No.
- **Model list CLI-aware?** Yes.
- **Writes:** `(commit)` updates `cliDefault` and `modelDefault`; submit sends those values in `createRegistryProject`.

### 9. Project Pipeline Step Agents

- **File:** `frontend/src/app/features/project-detail/components/project-detail/project-detail.html:273`
- **Shape:** `app-cli-model-selector` chip for every configurable LLM pipeline step.
- **CLI list filtered?** No.
- **Model list CLI-aware?** Yes.
- **Writes:** `(commit)` calls `ProjectDetailComponent.onStepAgentCommit`, which writes `cliType`, `model`, and `thinkingLevel` through `setProjectPipelineStep`.

### 10. Task Overview Pipeline Step Agents

- **File:** `frontend/src/app/features/task-detail/components/prompt-pane/overview-pane/overview-pane.component.html:363`
- **Shape:** compact `app-cli-model-selector` chip for editable aspect-step rows.
- **CLI list filtered?** No.
- **Model list CLI-aware?** Yes.
- **Writes:** `(commit)` calls `OverviewPaneComponent.onStepAgentCommit`, which writes `cliType`, `model`, and `thinkingLevel` through `setProjectPipelineStep`, then refreshes the pipeline projection.

### 11. Epic Rollup Agent

- **File:** `frontend/src/app/features/task-detail/components/epic-rollup-pane/epic-rollup-pane.component.html:81`
- **Shape:** `app-cli-model-selector` chip for an epic's agent config.
- **CLI list filtered?** No.
- **Model list CLI-aware?** Yes.
- **Writes:** `(commit)` emits `agentConfigCommit` to the epic overlay parent, which persists the epic task agent config.

## Related Controls Deliberately Out Of Scope

- `frontend/src/app/features/orchestrator/components/orchestrator-side-sheet/`
  hosts the **Orchestrator chat** selector through `<cac-chat>`. The execution
  route is intentionally GPT-only: Codex is enabled, while Claude and Gemini
  remain visible but disabled with the host-policy reason. This list does not
  depend on CLI quota snapshots.
- `frontend/src/app/features/project-detail/components/project-detail/project-detail.html:378` chooses per-CLI **permission mode**, not which CLI/model should run a task.
- Observability, quota, token, usage, and session panels filter or display CLI/model data but do not choose an execution agent.
- Project, workspace, lane, condition, and mode `<select>` elements are unrelated to CLI/model execution choice.

## Verification Notes

- `frontend/src/app/components/cli-model-selector/cli-model-selector.component.spec.ts` covers the shared popover behavior, keyboard arrow selection, all-CLI availability, and identical selector contract for the chat composer and code-review trigger IDs.
- The old bespoke `chat-model-badge` component is deleted; remaining comments that mention it describe historical behavior only.
