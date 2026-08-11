# Codex CLI fixtures

Captured `--json` frames from `codex exec --json ...`. See [`docs/system/cli/skills/cli-codex.md`](../../../../docs/system/cli/skills/cli-codex.md).

The Codex driver maps stable frames to the shared activity vocabulary while preserving structured `todo_list` frames verbatim for Trace. The typed event path normalizes those frames into plan snapshots.

Current captured fixtures:

- `todo-list-frame-family.jsonl` contains the started, updated, and completed lifecycle of one current Codex checklist item.
- `agt-2081-tool-router-exit.log` captures the duplicate router diagnostic emitted for a failed command.

Additional fixtures to capture when their wire shapes change:

- `session-meta.jsonl` — the first frame of a fresh `codex exec` (carries the session UUID; `OnOutputLine` reads `payload.id` from this).
- `tool-call-bash.jsonl`: a Bash-style tool invocation.
- `tool-call-read.jsonl`: a file-read tool invocation.
- `result.jsonl` — the final result frame.
