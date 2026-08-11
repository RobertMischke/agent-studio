# CLI fixture folder

Per-CLI captured output samples. The convention:

- One folder per CLI (`claude/`, `codex/`, `copilot/`, `gemini/`).
- Each fixture is a small file holding **one frame** (or one transcript snippet) so a regression test can name the case it locks. NDJSON streams are preserved as-is; pretty-printed JSON is fine for single frames.
- Add the matching test in `backend.Tests/<Cli>CliServiceTests.cs` (or `<Cli>QuotaProbeTests.cs` for quota fixtures) that loads the fixture and asserts the invariant.

Why fixtures: the per-CLI skill files under [`docs/system/cli/skills/`](../../../docs/system/cli/skills) document expected frame shapes; the fixtures are the executable copy. When a CLI version bump changes a frame, the fixture diff makes the change reviewable in one place.

When to add a fixture:

- A new frame type appears in `~/.runtime/cli-output/<cli>-<jobKey>.jsonl`.
- A regression in the wild that "looked weird" before we noticed — capture the exact lines.
- A CLI update changed the shape of an existing frame — replace the fixture in the same PR.

## Plan-frame deviation matrix

| CLI | Native frame | Native status | Normalized behavior | Corpus coverage |
|---|---|---|---|---|
| Codex | `item.started`, `item.updated`, and `item.completed` with `item.type=todo_list` | `completed: boolean` | Completed items become `done`; the first incomplete item is `active`; remaining items are `pending` | `codex/todo-list-frame-family.jsonl` |
| Codex (legacy) | `update_plan` | `pending`, `in_progress`, `completed` | `pending`, `active`, `done` | Inline adapter regression tests |
| Claude | `TodoWrite` | `pending`, `in_progress`, `completed` | `pending`, `active`, `done` | Adapter and plan-reader regression tests |
| Gemini | No native plan frame observed | Not applicable | No plan view is emitted; normal activity remains available | Adapter non-plan regressions |
| Copilot | No native plan frame observed | Not applicable | No plan view is emitted; normal activity remains available | Host fallback behavior |

This matrix is the compatibility boundary coordinated under AGT-2639/CAC-24 with the shared frame-corpus work. A CLI without a native plan family must degrade to an absent plan, never a guessed checklist.
