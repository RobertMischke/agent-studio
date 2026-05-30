# Mockups: Task Processing Pipeline (CI/CD-style)

Concept-stage UX mockups for [ADR-0051](../../adr/adr-0051-task-processing-pipeline.md). Two surfaces:

| File | Surface |
|---|---|
| [pipeline-editor.md](pipeline-editor.md) | Project-level pipeline editor: ordered pre/post steps, drag-reorder, per-step config, AI-assist. |
| [task-timeline.md](task-timeline.md) | Task-detail timeline: planned steps up front, live progress, per-step artifact + orchestrator verdict. |

## Why these are static (ASCII), not interactive HTML

The repo's standard is interactive HTML click-dummies ([AGENTS.md "Mockups must be interactive"](../../../AGENTS.md)). These mockups are **deliberately static ASCII** because this is a **concept task**: the deliverable is the ADR + data model + slicing plan, and the implementation is explicitly deferred to follow-up slices. The interactive HTML click-dummy is produced in **Slice 4** (the editor) and **Slice 1** (the timeline render) when the UI is actually built, at which point it sits next to the live components and the Playwright specs that lock them. Building an interactive HTML dummy now, before the data model is agreed, would be throwaway work that the slice rebuilds against the real signals.

These ASCII mockups exist to pin the *layout and information hierarchy* the slices implement against, not to be the click-dummy themselves.

## Design anchors

- Dark Catppuccin-inspired surface, consistent with the existing studio shell.
- Step states use the existing lane-glyph vocabulary: pending (hollow), running (pulse), ok (check), failed (cross), warn (triangle), skipped (dash).
- Menus are text-only ([AGENTS.md](../../../AGENTS.md) "Menu surfaces are text-only").
- Per-step model picker is the shared `<app-cli-model-selector>` (ASS-544/562), not a new picker.
- Editor mutations are optimistic ([ADR-0046](../../architecture-decisions.md#adr-0046)).
