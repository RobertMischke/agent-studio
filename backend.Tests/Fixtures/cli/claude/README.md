# Claude CLI fixtures

Captured `stream-json` frames from `claude -p ... --output-format stream-json --verbose`. See [`docs/system/cli/skills/cli-claude.md`](../../../../docs/system/cli/skills/cli-claude.md) for the frame catalogue.

Existing inline fixtures live in [`backend.Tests/ClaudeCliServiceTests.cs`](../../../ClaudeCliServiceTests.cs) for the common shapes. Use this folder when:

- A real-world frame is too large to inline.
- You need to replay a multi-frame transcript (NDJSON, one frame per line).
- A regression spans several frames and the test needs to load them as a unit.

Naming: `<purpose>.jsonl` (e.g. `rate-limit-overage.jsonl`, `tool-use-bash-multi-line.jsonl`).
