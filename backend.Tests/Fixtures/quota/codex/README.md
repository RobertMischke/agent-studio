# Codex quota fixtures

The versioned files are ANSI-stripped PTY captures of the real Codex `/status`
panel. Account, session, and scratch-directory identity were replaced with
stable placeholders; panel labels, line breaks, quota values, and version text
remain in the shape consumed by `CodexQuotaProbe`.

- `codex-status-v0.144.1.txt` was captured from the host-installed
  `codex-cli 0.144.1` on 2026-08-26.
- `codex-status-v0.149.0.txt` was captured on the same host from the pinned
  `@openai/codex@0.149.0` package without replacing the host installation.
- `codex-startup-v0.149.0.txt` preserves the update chooser that appeared
  before the 0.149.0 ready prompt.

Both versions presented an update chooser before the ready prompt. The probe's
startup steps are responsible for dismissing that chooser without selecting
the mutating `Update now` action.
