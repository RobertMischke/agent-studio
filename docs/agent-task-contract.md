# Agent Task Contract

Agent Task Processor owns the queue. A CLI agent owns only the single task that the application starts.

This contract is copied into watched target projects so Claude Code, Codex, GitHub Copilot, Gemini, and other coding agents understand the boundary.

## Controller Boundary

The application owns:

- Selecting the next ready task.
- Moving task folders between state lanes.
- Starting, stopping, and continuing CLI runs.
- Enforcing one active coding task per project.
- Recording CLI execution state, session ids, logs, summaries, and review transitions.

The agent owns:

- Reading the selected task's prompt.md.
- Implementing the requested change in the project source tree.
- Reading existing task evidence when resuming or recovering.
- Writing review evidence such as screenshots, result files, or short notes when project instructions require it.

## Agent Rules

When a CLI run starts, the application has already selected the task.

Agents must:

- Work on exactly the task they were given.
- Read the task prompt from the path provided by the runner.
- Treat the job folder as task-local evidence and context.
- Work in the project source tree for implementation changes.
- Keep screenshots that matter for review under the job folder's results/ directory.
- Preserve existing work when resuming or recovering a task.

Agents must not:

- Scan for other ready tasks.
- Pick another task.
- Move task folders between state lanes.
- Edit the state field in job.json.
- Start or continue another task on their own.
- Create branches, switch branches, merge branches, or manage worktrees unless the user explicitly changes the product boundary.

## State Model

The visible task states are:

```text
1-preparation -> 2-ready -> 3-progress -> 4-review -> 5-completed -> 6-archive
```

State transitions are application-controlled. A task can sit in `3-progress` without a live CLI process after a stop, crash, or backend restart. Treat the live CLI execution state as the real signal for whether work is currently running.

Only successful CLI runs move automatically from `3-progress` to `4-review`. Failed or stopped runs stay in `3-progress` so the user can inspect the log, restart, or continue the task.

## Task Files

Each task folder may contain:

- `job.json`: metadata owned by the application.
- `prompt.md`: task description and follow-up notes.
- `status.md`: generated review protocol owned by the application.
- `logs/cli-output.log`: durable CLI output.
- `attachments/`: input images or files supplied with the task.
- `results/`: output screenshots or files produced during the task.

Agents may read all task files. Agents may write evidence files when useful, but must not change queue state. Do not rely on hand-written `status.md` content for durable evidence because the application may regenerate it from logs.

## Output Contract (machine-read by the orchestrator)

The orchestrator parses CLI output for typed signals so it can decide what to do next without re-reading the agent's prose. Treat these as a hard contract, not a suggestion.

End every run with exactly one of these tokens on its own line:

- `[[TASK_DONE]]` - the work the user asked for is fully complete.
- `[[TASK_BLOCKED:<short reason>]]` - you cannot proceed; explain why briefly.
- `[[TASK_NEEDS_INPUT:<short reason>]]` - you need user input to continue.
- `[[TASK_NOOP]]` - you intentionally did nothing (rare; explain why).

Do not paraphrase the tokens or wrap them in code fences. Multiple tokens in a single run are not allowed; the orchestrator treats only the last one as authoritative.

When no token is emitted, the orchestrator falls back to a heuristic, marks the verdict as fallback, and posts an `Orchestrator` meta message into the chat so the user can see that the deterministic contract did not match.

When you receive a recovery prompt (the previous CLI session was lost), the user follow-up is the primary instruction. Do not reply "task done" without performing the follow-up; if you cannot perform it, emit `[[TASK_BLOCKED:<reason>]]`.

## Documentation Drift

After a CLI-executed task finishes, check whether README.md, ROADMAP.md, AGENTS.md, or docs need to be updated. Update them when the change affects product direction, public behavior, architecture, CLI contracts, filesystem contracts, or agent workflow. If no documentation update is needed, say so briefly in the task report.
