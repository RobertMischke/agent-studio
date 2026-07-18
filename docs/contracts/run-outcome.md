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

## Post-processing outcome taxonomy

Beyond the terminal-outcome wire value, the gate / escalation path classifies a
run that did NOT sign off cleanly into one of five buckets
(`PostProcessingOutcomeTaxonomy`, AGT-1944). The bucket - not the raw exit code -
decides what happens next:

| Bucket | Meaning | Routing |
|---|---|---|
| `success` | clean terminal verdict (DONE / NOOP) | accept |
| `code-defect` | a self-reported build / compile / test failure, or an agent process violation | normal reissue / human review as a code problem |
| `environmental` | failed on the host / provider / CLI, not the change | transient members retry with backoff; every member escalates flagged `environmental` |
| `inconclusive-with-results` | no terminal verdict, but files present in `results/` | human review WITH a "partial work to inspect" hint |
| `inconclusive-empty` | no terminal verdict and nothing in `results/` | the bare `5e-escalated` park |

**Environmental retry-with-backoff.** A *transient* environmental fault clears on
its own, so the orchestrator retries it with exponential backoff before
escalating instead of parking it on first detection:

- `EnvironmentalTransient` - a host file lock (the MSBuild `MSB3021` / `MSB3026`
  / `MSB3027` copy-lock family, "the process cannot access the file … because it
  is being used by another process") or a network glitch (DNS failure,
  `ECONNRESET` / `ETIMEDOUT` / `EAI_AGAIN`, a 502/503/504 gateway blip). Retried
  up to `PostProcessingOutcomeTaxonomy.DefaultMaxEnvironmentalRetries` (2) with a
  30s / 120s / 300s backoff, then escalated with the `environmental` category.
- `CliLaunchFailed` - the agent CLI could not launch or its `--resume` target was
  rejected (a dead session after a backend restart). Gets one automatic
  fresh-start retry (rebuild from disk), then escalates with the
  `cli-launch-failed` category. It no longer parks straight to `5e` on the first
  launch failure.

Non-retryable environmental members (`ModelInvalid`, `ContextOverflow`,
`QuotaExhausted`, `EnvironmentBlocker`, `AuthRefreshFailed`) still escalate on
first detection with their own honest categories (AGT-1941) - re-running would
hit the same wall.

- `AuthRefreshFailed` (AGT-2066 WÄCHTER / breaker) - the agent CLI could not
  launch because its OAuth session expired and the token refresh failed ("OAuth
  session expired and could not be refreshed"). Because every parallel clean-
  context run shares the one operator credential, this fails *every* launch
  identically until the credential is re-authenticated centrally - the incident
  that drained 17 cards in minutes on 2026-07-10. Classified *before* the generic
  `CliLaunchFailed` so it is NOT retried; escalates on first detection with the
  `auth-refresh-failed` category and a re-auth instruction. Pairs with the
  primary fix (credentials shared by link, not copied - see
  `docs/cli/supported-clis.md`).

Environmental cycles never accrue toward the per-task no-progress quarantine
streak (`RunQuarantineBreaker.CountsAsNoProgressFailure`), and a transient
environmental fault does not raise the crash toast while it is being retried.

**Per-attempt-chain reissue budget.** The shared reissue budget (spent by the
completion gate, evidence gate, and reissue-loop breaker) is counted per attempt
chain, not over the job's whole lifetime:
`ReviewDecisionOrchestrator.CountReissuesInCurrentChain` counts the `Reissue`
records recorded *since* the most recent chain-ending verdict (`Escalate` /
`AcceptAsDone`). A verdict that parks a card to human review or accepts it closes
the chain, so when a human reopens it the next attempt chain starts with a fresh
budget. Before this the count was sticky - a card whose budget was spent on an
earlier, already-resolved chain could never pass a budget-gated check again and
escalated on the first new concern (AGT-1935). In-chain behaviour is unchanged:
with no chain-ender in between, the per-chain count equals the old lifetime total.

**Escalation categories.** The system-initiated escalation funnel
(`HumanReviewEscalationCategories`) records WHY a card was parked. AGT-1944 adds
`environmental`, `cli-launch-failed`, and `inconclusive-with-results` to the
existing set; AGT-2066 adds `auth-refresh-failed` (a failed OAuth-session refresh
breaker); an inconclusive run picks `inconclusive-with-results` over the bare
`orchestrator-inconclusive` / `infra-crash` category when its `results/` dir is
non-empty.

`environmental-load` is reserved for support-agent calls affected by sustained
host CPU saturation. It is handled before a reviewing OneShot can become a gate
verdict: dispatch queues until cooling, uses a 3x timeout after the load phase,
and retries one timeout. It must not be reported as
`orchestrator-inconclusive` or charged to the card's reissue budget.

## Regression cover

`backend.Tests/RunOutcomeContractTests.cs` locks the contract end to end: each terminal sentinel drives lane, `status.md` `ProtocolResult`, and the failure toast from one classification, and `CodexExitMinusOne_WithSentinel_ClassifiesIdenticallyAcrossAllConsumers` feeds a rendered `cli-output.log` whose exit line reports the Windows kill artifact (`status=failed, exitCode=-1`) and asserts the sentinel still wins for every consumer. This is the case the divergence bug was named after. `FailedRunThatCommitted_RoutesToReviewAsCommittedPartial` and `FailedRunWithZeroCommits_StaysHardFailure` pin the commit-aware branch: a failed, sentinel-less run routes to review as `committed-partial` only when `commitsDuringRun > 0`, and still hard-fails at zero commits.
