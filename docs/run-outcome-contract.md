# Run Outcome Contract

The runner classifies a completed CLI invocation once, then every consumer reads that same classification.

## Contract

`TerminalRunOutcomeClassifier` maps the deterministic agent outcome plus process status to:

| Field | Used by |
|---|---|
| `Kind` | API/UI wire value: `success`, `failed`, `noop`, `blocked`, `needs-input`, `interrupted`, `unknown`. |
| `ProtocolResult` | The `status.md` `- Result:` line. |
| `ShouldMoveToReview` | `ProjectRunner` lane routing from `3-progress` to `4-auto-review`. |
| `ShouldShowFailureToast` | Frontend failure modal/toast gating. |

Hard sentinel matches win over process exit code. This is load-bearing on Windows: a killed or odd Codex process can report `exitCode=-1`, but if the agent emitted `[[TASK_NOOP]]`, the run outcome is `noop`, not `failed`.

## Consumer Rules

- Lane routing must call `RunCompletionPolicy.ShouldMoveToReview(TerminalRunOutcome)`.
- Summary generation must enforce `ProtocolResult` after the Haiku summary is produced.
- UI failure surfacing must use the `runOutcome` field when present and fall back to legacy `execution.status === 'failed'` only when it is absent.
- Raw process status and exit code remain visible for diagnostics, but they do not override a terminal sentinel.

## Expected Cases

| Agent signal | Process status / exit | Kind | Protocol result | Lane | Failure toast |
|---|---:|---|---|---|---|
| `[[TASK_DONE]]` | any | `success` | `Success` | `4-auto-review` | no |
| `[[TASK_NOOP]]` | any | `noop` | `NoOp` | `4-auto-review` | no |
| `[[TASK_BLOCKED:...]]` | any | `blocked` | `Blocked` | `4-auto-review` | no |
| `[[TASK_NEEDS_INPUT:...]]` | any | `needs-input` | `NeedsInput` | `4-auto-review` unless auto-mode intercepts it first | no |
| no terminal signal | `failed` | `failed` | `Failed` | stays in `3-progress` | yes |
| deliberate stop | `stopped` | `interrupted` | `Failed` | stays in `3-progress` | no |

`Partial` is reserved for completed runs that reached review but could not be classified confidently.

## Regression cover

`backend.Tests/RunOutcomeContractTests.cs` locks the contract end to end: each terminal sentinel drives lane, `status.md` `ProtocolResult`, and the failure toast from one classification, and `CodexExitMinusOne_WithSentinel_ClassifiesIdenticallyAcrossAllConsumers` feeds a rendered `cli-output.log` whose exit line reports the Windows kill artifact (`status=failed, exitCode=-1`) and asserts the sentinel still wins for every consumer. This is the case the divergence bug was named after.
