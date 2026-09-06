# Codex CLI fixtures

Captured `--json` frames from `codex exec --json ...`. See [`docs/system/cli/skills/cli-codex.md`](../../../../docs/system/cli/skills/cli-codex.md).

The Codex driver currently passes frames through unchanged (no `TransformReadLine` translation yet — known gap). When the transform lands, the regression-test base lives here.

`models-0.151.0.json` and `models-0.153.4.json` capture the relevant
`codex debug models` shapes before and after GPT-6 Astra appeared. Parser tests
use them to lock the registry-union availability rule and CLI-derived reasoning
ladder.

Suggested first fixtures to capture:

- `session-meta.jsonl` — the first frame of a fresh `codex exec` (carries the session UUID; `OnOutputLine` reads `payload.id` from this).
- `tool-call-bash.jsonl` — a Bash-style tool invocation, for the upcoming `TransformReadLine` switch.
- `tool-call-read.jsonl` — file-read tool, same reason.
- `result.jsonl` — the final result frame.
