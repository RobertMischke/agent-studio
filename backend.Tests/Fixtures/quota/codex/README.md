# Codex quota fixtures

These files are scrubbed ANSI-free PTY captures of the real Codex `/status`
panel. The CLI version is part of each filename so an output change is added as
a new compatibility fixture instead of silently replacing the old contract.

Capture procedure:

1. Run the exact `@openai/codex` version in a PTY using the normal logged-in
   account.
2. Wait for the interactive prompt and slash-command menu to be ready.
3. Select `/status` from the menu.
4. Strip ANSI cursor/control sequences and scrub account, path, and session
   identifiers. Do not rewrite labels, line breaks, or box layout.

`codex-status-v0.144.1.txt` was captured on 2026-08-25 and
`codex-status-v0.149.0.txt` on 2026-08-26. Both versions render reset
timestamps on continuation lines and may omit the standard 5-hour row.
