# Codex quota fixtures

The versioned files are ANSI-stripped PTY snapshots captured from the real
host CLIs on 2026-08-26. Both interactive CLIs were opened in an isolated
temporary directory and `/status` was submitted as a command.

The account and session identifiers are redacted. All quota-panel labels,
line breaks, bars, percentages, reset labels, and CLI version text are retained
in the shape consumed by `CodexQuotaProbe`.

The captures show the current split-line reset form and the valid case where
the standard 5-hour window is absent while the standard weekly and both Spark
windows remain present. Inline tests in `CodexQuotaProbeTests` retain the older
same-line reset form as a parser fallback.
