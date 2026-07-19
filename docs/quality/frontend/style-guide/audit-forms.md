# Audit — Form Controls

Input / textarea / select surfaces. **No Angular canonical exists today.** Form controls are either bare `<input>` / `<select>` / `<textarea>` with feature-scoped SCSS, or a feature-specific wrapper class.

## Inventory

35 `<input>` usages, 26 `<select>` usages, 10 `<textarea>` usages across `frontend/src/app/`. Common class patterns:

| Class / wrapper                                 | Where                                                            |
| ----------------------------------------------- | ---------------------------------------------------------------- |
| `.board-search-icon__field`, `.board-search-icon__input` | `features/board/components/board-search-icon/`           |
| `.chat__input`, `.chat-compose__input`, `.composer__input` | `components/chat/`, orchestrator side sheet               |
| `.cli-config__input`                            | `features/job-detail/components/cli-config-card/`                |
| `.client-filter__select`                        | filter dropdown                                                  |
| `.commandbar__field`, `.commandbar__select`     | command bar                                                      |
| `.field__input`, `.field__textarea`             | create-task dialog                                               |
| `.logic__control`, `.logic__select`             | orchestrator logic panel                                         |
| `.model-picker__select`                         | model picker                                                     |
| `.obs__field`, `.par__select`, `.prt__field`, `.pws__select`, `.session-followup__input`, `.sheet__select` | various panels |
| `.ov-title-input`                               | overview-pane                                                    |

Geometry observations:

- Height: mostly 28-32px, some 36px in dialog bodies.
- Background: `--studio-bg-elevated` is the common choice; some use `--studio-bg-editor`.
- Border: `--studio-border` or `--studio-border-strong`; some inputs use a focused border `--studio-accent`.
- Border-radius: 3-6px, no single standard.
- Padding: 4-8px inline, no single standard.

## Findings

This is the **least-converged family** in the codebase. Every dialog and every panel has its own input style. The deltas are small enough that a single `<app-input>` / `<app-select>` / `<app-textarea>` set with `size=sm|md` would cover ~80% of the sites; the rest are search-as-you-type combo controls (board-search, command-bar) that are intentionally bespoke.

## Migration consideration

Extracting form-control canonicals is **lower priority than buttons and pills** because:

1. The visual delta between sites is smaller (every input is "a thin rectangle with a focus ring") — the inconsistency is less loud.
2. Angular form controls need ControlValueAccessor wiring; a "good" `<app-input>` is meaningfully more code than a "good" `<app-button>`.
3. The next big visual issue is the modal padding (this task) and the button family (queued in [migration-status.md](./migration-status.md)).

The **minimum viable convergence** is a set of CSS-level token aliases (`--studio-input-height`, `--studio-input-padding-inline`, `--studio-input-radius`) that every form-control SCSS reads. That gets visual consistency without committing to ControlValueAccessor.

Decision deferred to a follow-up task — see [migration-status.md](./migration-status.md) "F-Forms: Decide canonical".
