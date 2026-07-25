# Claude quota fixtures

`claude-usage-v2.1.202-api-billing.txt` is the ANSI-stripped PTY snapshot
captured from the real host CLI on 2026-07-23:

```text
claude --version
2.1.202 (Claude Code)
```

The interactive CLI was opened in an isolated temporary directory and `/usage`
was submitted. The fixture retains the quota panel text exactly as consumed by
`ClaudeQuotaProbe`; welcome-banner identity and terminal cursor-control bytes
were intentionally excluded.
