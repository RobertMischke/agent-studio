# Codex quota fixtures

These are ANSI-stripped `/status` PTY captures. Account identifiers, session
identifiers, and local paths are sanitized; layout, labels, line breaks, quota
values, model names, and CLI versions are preserved.

- `codex-status-v0.144.1.txt`: captured with the locally installed Codex CLI.
- `codex-status-v0.149.0.txt`: captured via `npx @openai/codex@0.149.0` using
  the existing authenticated Codex home on 2026-08-25.

The version is part of each filename so an output-format change is introduced
as a new fixture and expectation instead of overwriting prior compatibility.
