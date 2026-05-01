# Roadmap

Agent Task Processor is a local control layer for keeping coding agents busy without turning a project into an orchestration platform.

The product goal is simple: keep one coding task moving per project, reduce human babysitting, and make review easier.

## Product Thesis

Modern coding agents are useful for long-running implementation work, but they still need a steady queue, clear handoffs, and fast human review. Agent Task Processor turns that loop into a local board:

- The human defines and reviews work.
- The board keeps the queue visible.
- The runner starts the next ready task automatically.
- The agent writes evidence back into the task folder.

The product should feel like a workbench, not a command center. It should make one project easier to move through a sequence of tasks, then scale that same pattern across several projects.

## Current Shape

Today the application provides:

- A .NET backend and Angular PWA for local use.
- Watched project folders with ordered task states.
- One active coding task per project.
- Parallel execution across different watched projects.
- CLI execution for Claude Code, Codex, GitHub Copilot, and Gemini.
- Live task output, protocol summaries, screenshots, and review evidence.
- CLI quota and session visibility where the underlying tools expose enough data.

## Roadmap Themes

### Project Control

Make each watched project easier to inspect and operate:

- Project detail pages with path, configuration, status, and quick actions.
- Clearer manual start vs. auto-pickup behavior.
- Safer locking once a task has started, so completed or running work does not drift to another project by accident.
- Better visibility into active CLI sessions that may already be working in the same project.

### Task Finding And Shape

Make large boards easier to understand:

- Search across titles, prompts, metadata, and relevant task fields.
- Project-level tags with defaults such as Backend, Frontend, UI Improvement, and Bugfix.
- Better ordering interactions with stronger drag feedback and less visible internal bookkeeping.
- Cleaner archive browsing for completed and historical work.

### Roadmap And Intent

Turn a pile of tasks into a useful product view:

- A project roadmap view that groups open tasks by theme.
- Automatic intent extraction from task prompts.
- Follow-up prompts such as "what should be next?", "what is duplicated?", or "what should be split?"
- A path from planning output into a new task draft.

### Agent Feedback

Make agent work easier to judge while it is still running:

- Short protocol summaries at the top of the detail view.
- Mid-run status requests where a CLI supports safe intervention.
- Stronger Activity Log parsing across all supported CLIs.
- Better usage, quota, and model feedback, including edge cases such as model-specific limits.

### Focused UX

Keep the app dense, fast, and pleasant to use:

- Compact headers and status bars.
- Better model and CLI defaults.
- Completion notifications that do not interrupt the workflow.
- Layout polish for detail panes, rows, cards, tooltips, and screenshots.

## Hard Boundaries

The core execution model stays intentionally narrow:

- One coding task runs per project at a time.
- Parallelism is allowed across projects, not inside one project.
- The app does not create branches, switch branches, merge branches, or manage worktrees.
- The app does not become a workflow engine.
- Runtime job artifacts belong in watched task folders, not in this source repository.

Planning and research tasks may eventually have a different concurrency model because they do not change source code. That distinction must stay explicit. Coding tasks keep the one-at-a-time rule.

## Agent Decision Principles

When changing this product, prefer work that:

- Reduces human babysitting.
- Improves review quality.
- Makes the current task state easier to see.
- Preserves the sequential per-project execution model.
- Uses local files and existing subscriptions instead of new hosted infrastructure.
- Keeps the UI compact, legible, and calm.

Be cautious with work that:

- Adds bookkeeping before it removes friction.
- Turns a simple queue into a workflow system.
- Encourages multiple coding agents to edit one project at the same time.
- Hides important evidence from the reviewer.

## Documentation Drift

After any CLI-executed task finishes, check whether the README, this roadmap, AGENTS.md, or docs need to be updated. Update them in the same task when the change affects product direction, public behavior, architecture, CLI contracts, filesystem contracts, or agent workflow. If no documentation update is needed, say so briefly in the task report.
