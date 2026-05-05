# Visual Audit Of Current Stable Chats

Stable was inspected with Playwright on May 5, 2026. The goal was not to judge individual task quality, but to understand how the current chat UI feels when real finished jobs are opened.

## Evidence Cases

| Job | State | Turns | Tool pills | Tool chips | Visual conclusion |
|-----|-------|-------|------------|------------|-------------------|
| `chat-read--grep-wiederholunge-mit-weight-darstellen` | `6-archive` | 245 | 114 | 148 | Tool evidence dominates the view. Great for debugging, too dense for normal reading. |
| `chat-wechsel-zwischen-projekten-nicht-gut-moeglich` | `4-review` | 199 | 93 | 124 | Actor identity and task list chrome compete with the actual conversation. |
| `project-analysis-reports-surface` | `4-review` | 82 | 39 | 60 | More readable, but still technical by default and not enough human summary. |

Screenshots live in [evidence/](evidence/).

## What Is Visually Wrong Today

1. **The technical layer is default.** Raw tool calls, markers, paths, and parser-shaped text appear before the human story.
2. **Actors are not obvious enough.** The user, task agent, orchestrator, supervisor, system, and tools need a clearer grammar.
3. **Task and run state are too prominent in the wrong places.** Task metadata is important, but the default chat should not become a task-management dashboard.
4. **Tool chips cost too much vertical and visual attention.** A tool-heavy run should read as one compact burst until expanded.
5. **Overlays compete with controls.** Existing Stable failures show visible controls that cannot be clicked because another layer intercepts pointer events.
6. **Screenshots are not treated as first-class review material.** They should be browsable through artifacts and thumbnails rather than buried in raw protocol text.
7. **There is no dedicated developer history view.** The first dashboard mockup showed useful debugging potential, but it should not be the primary chat.

## Revised Principle

Default chat should be human-readable:

- Plain language first.
- Compact participant turns.
- Task boundaries as subtle markers.
- Tool, decision, artifact, warning, and report rows as compact events.
- Technical trace behind details, Trace, Inspector, or Verbose Debug.

Developer debugging should be powerful:

- Fullscreen.
- Read-only.
- Timeline and actor activity.
- Duration, token usage, tool density, warning density, task markers, and orchestrator explanations.
- Links back to raw trace and artifacts.

## Good Defaults

| Current Stable pattern | New default |
|------------------------|-------------|
| Many tool chips in transcript | One inline tool burst row |
| Technical paths in visible prose | Human summary, path list behind details |
| Large task/run cards | Thin task markers and run separators |
| Orchestrator text as another message | Inline decision row with expandable evidence |
| Debugging mixed into reading | Verbose Debug fullscreen |
| Banners overlapping controls | Reserved layout zones and Playwright pointer tests |

