# Codex quota fixtures

These files are normalized, ANSI-free PTY snapshots of the Codex `/status`
panel. The CLI version is part of each filename so a layout change is added as
a new fixture instead of silently weakening the parser for every version.

Capture contract:

1. Record `codex --version`.
2. Start that exact CLI in a PTY and run `/status`.
3. Strip ANSI screen-control sequences and replace account, directory, and
   session identifiers with stable placeholders.
4. Preserve panel labels, line breaks, quota bars, percentages, and reset text.

`codex-status-v0.149.0.txt` was captured from the published
`@openai/codex@0.149.0` package on 2026-08-27. In that live account state the
standard 5-hour row was absent, while the standard Weekly row and both Spark
rows were present. The parser must not promote a Spark row into the missing
standard bucket.
