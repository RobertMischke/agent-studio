# Run Outcome Contract

The runner classifies a completed CLI invocation once, then every consumer reads that same classification.

## Contract

`TerminalRunOutcomeClassifier` maps the deterministic agent outcome plus process status to:

| Field | Used by |
|---|---|
| `Kind` | API/UI wire value: `success`, `failed`, `noop`, `blocked`, `needs-input`, `interrupted`, `committed-partial`, `unknown`. |
| `ProtocolResult` | The `status.md` `- Result:` line. |
| `ShouldMoveToReview` | `ProjectRunner` lane routing from `3-progress` to `4-auto-review`. |
| `ShouldShowFailureToast` | Frontend failure modal/toast gating. |

Hard sentinel matches win over process exit code. This is load-bearing on Windows: a killed or odd Codex process can report `exitCode=-1`, but if the agent emitted `[[TASK_NOOP]]`, the run outcome is `noop`, not `failed`.

## Consumer Rules

- Lane routing must call `RunCompletionPolicy.ShouldMoveToReview(TerminalRunOutcome)`.
- Summary generation must enforce `ProtocolResult` after the Haiku summary is produced.
- UI failure surfacing must use the `runOutcome` field when present and fall back to legacy `execution.status === 'failed'` only when it is absent.
- Raw process status and exit code remain visible for diagnostics, but they do not override a terminal sentinel.
- **UI head-state precedence.** A single run outcome is not the leading head state on the task-detail surface: the *current lane / review decision leads*. The protocol pane's verdict pill (`frontend/.../protocol-pane/protocol-verdict.ts`) demotes a run-outcome `Blocked`/`Failed` to a collapsed "superseded run outcome" history strip once the card reaches an accepted stand (`orchestratorVerdict === 'accept'`, or lane `6-completed`/`7-archive`). A `Blocked` from an overhauled run context must never contradict an accepted stand as the head banner. See `frontend/src/app/features/task-detail/README.md` ("Protocol verdict precedence").
- **UI verdict chain.** Beneath the head pill, the protocol pane renders a visible **verdict chain** (`frontend/.../protocol-pane/protocol-verdict-chain.ts` → `deriveVerdictChain`): `Run → Gate → Review aspects → Lane decision`, each step statused and linked to its evidence, with the *leading* step marked (the lane decision when one exists). A one-line causal narrative connects the earlier steps to that leading decision so a reviewer can see *why* the head state is what it is (e.g. why a card was escalated to a human even though the run's automated checks passed). The chain is derived from the same signals as the head pill, so the two never disagree.

## Expected Cases

| Agent signal | Process status / exit | Kind | Protocol result | Lane | Failure toast |
|---|---:|---|---|---|---|
| `[[TASK_DONE]]` | any | `success` | `Success` | `4-auto-review` | no |
| `[[TASK_NOOP]]` | any | `noop` | `NoOp` | `4-auto-review` | no |
| `[[TASK_BLOCKED:...]]` | any | `blocked` | `Blocked` | `4-auto-review` | no |
| `[[TASK_NEEDS_INPUT:...]]` | any | `needs-input` | `NeedsInput` | `4-auto-review` unless auto-mode intercepts it first | no |
| no terminal signal, no commits | `failed` | `failed` | `Failed` | stays in `3-progress` | yes |
| no terminal signal, committed work | `failed` (exit `-1`) | `committed-partial` | `Partial` | `4-auto-review` | no |
| deliberate stop | `stopped` | `interrupted` | `Failed` | stays in `3-progress` | no |

`Partial` is reserved for runs that reached review but could not be classified confidently: a completed-but-unrecognized run (`unknown`), or a run that committed real work yet exited non-zero without a sentinel (`committed-partial`).

### Committed-partial

When a run exits non-zero **without** a terminal sentinel but `git rev-list HeadShaBefore..HeadShaAfter` shows it committed at least one change, the non-zero exit is almost always a killed downstream step (classically the watchdog terminating a post-commit test run, which on Windows reports `exitCode=-1`) rather than a genuine crash. Hard-failing such a run would discard a real commit, re-loop the card via reissue, and trip the auto-failure circuit breaker that flips the runner to manual mode. Instead the classifier routes it to `4-auto-review` as `committed-partial` (`ProtocolResult: Partial`, no crash toast). A no-sentinel card in `4-auto-review` is left for a human by `ReviewDecisionOrchestrator` — it is never auto-reissued — so auto-continuous mode is preserved. A failed run with **zero** commits is unaffected and still hard-fails.

## Regression cover

`backend.Tests/RunOutcomeContractTests.cs` locks the contract end to end: each terminal sentinel drives lane, `status.md` `ProtocolResult`, and the failure toast from one classification, and `CodexExitMinusOne_WithSentinel_ClassifiesIdenticallyAcrossAllConsumers` feeds a rendered `cli-output.log` whose exit line reports the Windows kill artifact (`status=failed, exitCode=-1`) and asserts the sentinel still wins for every consumer. This is the case the divergence bug was named after. `FailedRunThatCommitted_RoutesToReviewAsCommittedPartial` and `FailedRunWithZeroCommits_StaysHardFailure` pin the commit-aware branch: a failed, sentinel-less run routes to review as `committed-partial` only when `commitsDuringRun > 0`, and still hard-fails at zero commits.
