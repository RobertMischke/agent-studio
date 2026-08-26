# Codex quota fixtures

These files are ANSI-stripped `/status` PTY captures from real Codex CLI
versions. Each CLI was launched in an isolated temporary directory and the
slash command was submitted only after the ready prompt appeared.

- `codex-status-v0.130.0.txt` was captured from `codex-cli 0.130.0` on
  2026-08-26.
- `codex-status-v0.149.0.txt` was captured from `codex-cli 0.149.0` on
  2026-08-26.

Account identity, session UUID, and the temporary directory were replaced by
stable placeholders. The panel labels, ordering, percentages, and reset shapes
are retained exactly as consumed by `CodexQuotaProbe`. In both captures the
standard 5-hour row is legitimately absent while the standard weekly row and
both Spark rows are present.
