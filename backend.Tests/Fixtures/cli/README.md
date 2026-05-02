# CLI fixture folder

Per-CLI captured output samples. The convention:

- One folder per CLI (`claude/`, `codex/`, `copilot/`, `gemini/`).
- Each fixture is a small file holding **one frame** (or one transcript snippet) so a regression test can name the case it locks. NDJSON streams are preserved as-is; pretty-printed JSON is fine for single frames.
- Add the matching test in `backend.Tests/<Cli>CliServiceTests.cs` (or `<Cli>QuotaProbeTests.cs` for quota fixtures) that loads the fixture and asserts the invariant.

Why fixtures: the per-CLI skill files under [`docs/cli-skills/`](../../../docs/cli-skills/) document expected frame shapes; the fixtures are the executable copy. When a CLI version bump changes a frame, the fixture diff makes the change reviewable in one place.

When to add a fixture:

- A new frame type appears in `~/.runtime/cli-output/<cli>-<jobKey>.jsonl`.
- A regression in the wild that "looked weird" before we noticed — capture the exact lines.
- A CLI update changed the shape of an existing frame — replace the fixture in the same PR.
