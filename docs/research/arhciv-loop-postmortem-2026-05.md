# `arhciv-besser-darzustellen` 31-run loop — post-mortem & fix (2026-05-03)

## Symptom

User saw a 33-run activity log on the live backend with 1 ok, 1 fail,
and 31 "unknown" outcomes for the same job. The job spent ~23 minutes
spinning continues against the same captured session UUID
(`dacb0f58-8508-43f4-99ba-93b0f7b6775c`) without ever reaching review,
without ever changing a file in the working tree, and without the user
intervening.

> "ich bin hier gerade an meinem aktuellen Task, an dem ich arbeite, und
> der läuft hier gerade in einer Endlosschleife."

## Reconstructed timeline

Source: `agent-taskboard-workspace/projects/agent-taskboard/4-review/arhciv-besser-darzustellen/logs/{cli-output.log,session-events.jsonl}`.

| Wallclock | Event | Notes |
|-----------|-------|-------|
| 17:49:16 | start, captured `dacb0f58` | First real run; failed at 17:50:13 (cascade Stop) |
| 18:13:17 | continue, **CapturedSessionId=null** | 1st of the loop |
| 18:14 .. 18:36 | 30 more continues, all CapturedSessionId=null, Resumed=true, InputSessionId=`dacb0f58` | ~30 s cadence |
| 18:36:44 | continue, captured `dacb0f58` back | Loop exits naturally |
| 18:37:32 | exit, status=completed, [[TASK_BLOCKED:…]] |

`session-events.jsonl` recorded 31 continues with `CapturedSessionId=null`. None of them appear in `cli-output.log` past the synthetic `[taskboard] Started` line — they spawned, ran briefly, exited without emitting a `● Session init <uuid>` line, and the recovery path that should have invalidated the dead session never fired. Each run kept resuming the same dead UUID.

## Root cause: race in capture-fail recovery

[`backend/Services/Runner/ProjectRunner.cs#OnCliFinishedAsync`](../../backend/Services/Runner/ProjectRunner.cs) had this read:

```csharp
var resumeTargetWasGone =
    _activePlan?.ResumeFlag == true
    && !string.IsNullOrWhiteSpace(_activePlan.SessionToResume);
if (resumeTargetWasGone) {
    _sessions.SetJobSessionName(jobId, null, Entry.Path);
    _sessions.MarkSessionChainRecovery(jobId, Entry.Path);
}
```

The decision read **`_activePlan` directly from a shared field**. Two concurrent paths can null that field before this read:

1. The re-issue branch (`OutcomeActionKind.ReissueWithStrongerFraming`) at line 1067 — clears `_activePlan = null` and schedules the re-issue on the thread pool.
2. `RunOrchestratorDecisionAsync` at line 695 — clears `_activePlan = null` before the orchestrator HTTP call.
3. A new `RunCliAsync` re-entry from a tick — sets `_activePlan = newPlan`, overwriting the run's plan.

`OnCliFinishedAsync` itself runs on the thread pool (`Task.Run` from `OnCliFinished`). When the capture-fail path lost the race and read `null`, `resumeTargetWasGone` became `false`, the recovery marker was never appended, and `sessionName` stayed equal to the dead UUID. The next pickup resumed it and capture-failed identically — for 31 turns.

The `[capture-fail] No claude session id from this run; next follow-up will rebuild from disk.` chat line in the user's screenshot is the giveaway: that's the message the false-branch produces, even though the just-finished run was unambiguously a resume against a real UUID.

## Why this loop went unbounded

Three layers should have stopped it before 31 iterations and none did:

- **Recovery marker** — would have flipped the next pickup to the planner's Recovery branch. Lost the race.
- **`StuckLoopGuard`** — only ticks on `outcome.Kind == NeedsInput`. The capture-fail runs produced no agent text and were classified `Unknown`, so the guard never advanced.
- **`AutoFailureHaltThreshold`** (added earlier this session) — only counts `RunIntent.AutoPickup`. The continues here were `RunIntent.UserContinue` driven by the auto-orchestrator-loop, so the cap was never tested.

## Fix

Two changes in `ProjectRunner`, plus an extracted helper for direct test coverage.

### A. Snapshot the plan at the top of `OnCliFinishedAsync`

```csharp
var planSnapshot = _activePlan;
var intentSnapshot = _activeIntent;
var followupSnapshot = _activeFollowup;
var reissueAttemptSnapshot = _activeReissueAttempt;
```

Every read in the body now uses these locals, including the capture-fail block, the `RunOutcomePolicy.Decide` call, and the re-issue branch. Concurrent clears of the fields no longer affect this run's recovery decision.

### B. Per-job consecutive capture-fail circuit-breaker

```csharp
internal const int CaptureFailHaltThreshold = 3;
private string? _consecutiveCaptureFailJobId;
private int _consecutiveCaptureFailCount;
```

Three consecutive capture-fails on the same job halt auto-mode (`SetMode("manual")`) and post a chat decision message. Reset on any successful capture. This is the structural fallback if the recovery marker write or the planner ever fails to consume it — the live trace recorded **31** capture-fails; this caps it at **3**.

Three was chosen so a single transient cold-cache failure plus its retry does not flap the runner.

### C. Extracted pure helper for testability

```csharp
internal static bool ShouldMarkSessionChainRecovery(RunPlan? planSnapshot) =>
    planSnapshot?.ResumeFlag == true
    && !string.IsNullOrWhiteSpace(planSnapshot.SessionToResume);
```

Pinned by 5 unit tests in [`backend.Tests/AutoPickupCascadeTests.cs`](../../backend.Tests/AutoPickupCascadeTests.cs), including the exact arhciv-shape input.

## What this does NOT fix

- The original `dacb0f58` session loss at 17:49 — that was the cascade kill from the prior post-mortem. The fix here ensures one such event doesn't snowball into 31 wasted continues.
- AutoCommit not running for "Agent Task Processor" — `project-settings.json` has `AutoCommit: false`. That's a separate configuration issue (toggle in the UI or default change); tracked outside this fix.
- Stable backend pointing at the dev checkout via `appsettings.Local.json` — separate config drift, fix is to correct the WatchPath RootPath. The user should change stable's `WatchPaths[].RootPath` to `C:\Projects\agent-taskboard-devspace\agent-taskboard-stable`.

## Verification

- 13 / 13 `AutoPickupCascadeTests` pass
- 101 / 101 affected suites pass (TaskRunnerPlanTests + RunOutcomePolicyTests + SessionEventsTests + AutoPickupCascadeTests)
- Build clean

## Files touched

- `backend/Services/Runner/ProjectRunner.cs` — snapshot block, `ShouldMarkSessionChainRecovery`, `CaptureFailHaltThreshold`, capture-fail circuit-breaker.
- `backend.Tests/AutoPickupCascadeTests.cs` — 6 new tests (5 helper + 1 threshold pin).
- `docs/research/arhciv-loop-postmortem-2026-05.md` — this document.
