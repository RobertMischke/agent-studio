# Gemini CLI fixtures

Captured `stream-json` frames from `gemini -p ... -o stream-json --skip-trust -y`. See [`docs/cli-skills/cli-gemini.md`](../../../../docs/cli-skills/cli-gemini.md).

Inline fixtures already live in [`backend.Tests/GeminiCliServiceTests.cs`](../../../GeminiCliServiceTests.cs) for the common frame shapes (`init`, `message`, `tool_use`, `tool_result`, `result`). Use this folder when:

- You hit the buffered-stdout limitation (see skill `§ Known limitation`) and want a captured PTY transcript for the eventual fix.
- You observe a new tool name we haven't mapped (Gemini's `ToolRegistry` ships built-ins, but extensions / MCP can add more).
- A `result` frame carries new `stats` fields we want to surface in the marker.
