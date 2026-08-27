# Codex quota fixtures

Each fixture is an ANSI-stripped PTY `/status` transcript named with the CLI
version that produced its panel shape.

- `codex-status-v0.149.0-split-reset-lines.txt` was captured from
  `@openai/codex` 0.149.0 on 2026-08-27 in an isolated temporary directory.
  Account identity and the session UUID are redacted; labels, line breaks,
  percentages, reset text, model names, and panel ordering are unchanged.
- `codex-status-v0.135.0-inline-reset-lines.txt` preserves the earlier panel
  variant whose reset text appeared on the same row. It remains a parser
  fallback contract.

Adding a fixture for every observed CLI panel version makes output drift fail
the parser test before it becomes an empty or canceled quota card.
