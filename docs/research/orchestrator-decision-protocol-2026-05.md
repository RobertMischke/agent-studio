# Orchestrator decision protocol: continuous review of running CLIs (2026-05-06)

## Problem

The orchestrator only looks at agent output at run end. While a CLI is still
running, an agent that emits `[[TASK_NEEDS_INPUT:...]]` (or `[[TASK_BLOCKED:...]]`)
is invisible to the user until the run finishes and the post-run policy fires.
Today such moments only show up in the activity log feed; nothing in the
project view stands out.

The user's framing: "Wenn der Task einen Output hat, das sagt: 'Ich brauch
jetzt das und das hier ist eine Decision', dann ist das irgendwie so ein
Major Punkt. Das muss auch in der README oder Analyse halt für mich als
Menschen dann sehr, sehr gut sichtbar sein und herausstechen."

## The open question

The brief asks whether the orchestrator needs a *typed channel* between the
CLI agent and the runtime (e.g. a JSON event stream over a side socket, a
named pipe carrying `decision-required` events) or whether *scanning recent
output every N seconds* is enough.

The agent contract today (ADR-0002, [docs/contracts/agent-task.md](../contracts/agent-task.md))
already pins the answer for the post-run path: the canonical sentinels
(`[[TASK_DONE]]`, `[[TASK_BLOCKED:...]]`, `[[TASK_NEEDS_INPUT:...]]`,
`[[TASK_NOOP]]`) are bracketed strings the agent emits in its normal stdout.
They are loose on whitespace and case so they survive prompt drift and
intermediate framing. `AgentOutcomeAnalyzer.SentinelRegex` is the single
authority for how they parse.

## Recommendation: scan, do not channel

We recommend **continuous output scanning** of the existing CLI buffer for
the same canonical sentinels, on the same 5 s pickup tick the runner already
runs, with the result published as a typed pending-decision record on the
project. We explicitly do **not** introduce a typed side channel.

Why:

1. **Same grammar end-to-end.** ADR-0002 anchors the deterministic-orchestration
   philosophy on a single sentinel grammar. Adding a second
   "decision-while-running" channel would split the agent contract into two
   sub-contracts (stdout sentinels at run end, side-channel events mid-run)
   with two grammars to keep in sync. Sentinel parsing is already the load-bearing
   primitive `AgentOutcomeAnalyzer.SentinelRegex` exposes; reuse it.
2. **Every supported CLI already writes stdout.** Claude, Codex, Copilot, and
   Gemini all emit the agent's response on stdout (per the per-CLI driver
   contract in `docs/cli/supported-clis.md`). A typed channel would have to be
   reimplemented per CLI driver - and at least one (Copilot) does not give
   us a structured event stream we control. Stdout works for all four with
   no driver changes.
3. **The output buffer is already in memory.** Each `CliExecutionServiceBase`
   keeps a per-job `OutputBuffer: ConcurrentQueue<CliOutputLine>` and a
   persisted `cli-output.log` mirror. The runner's 5 s pickup tick already
   walks per-project state. Scanning the tail of that buffer is cheap (last
   K lines, regex over agent stream only) and adds no new I/O.
4. **The brief explicitly says "piggyback".** The constraint section: *"The
   5 s cadence is a target, not a contract. The CLI output buffer poll is
   already happening at a similar rate; piggyback on that path rather than
   adding a parallel ticker."* A typed channel would *be* the parallel
   ticker we are told to avoid.
5. **Scope of the signal.** A "decision required" notification is structural
   (sentinel matched / did not match), not heuristic. We are not trying to
   classify free-form English; we are looking for a literal bracketed token.
   That is exactly the signal stdout scanning is good at.

A typed channel becomes the right answer only if a future CLI stops printing
agent output to stdout, or if we need richer mid-run telemetry (per-step
progress percentages, structured tool-call events) that would not survive
the round-trip through line-buffered text. Neither is true today.

## Protocol

### Detection

- **Source.** The live `OutputBuffer` for the project's active job (from
  `ICliExecutionService.GetOutput(jobKey)`). Falls back to the persisted
  `cli-output.log` only when a backend restart cleared the in-memory buffer
  while a job remains in `3-progress`.
- **Tail size.** Last `K = 200` lines is enough headroom for any reasonable
  agent's final emission while keeping the regex pass O(K) per tick.
- **Regex.** Reuse `AgentOutcomeAnalyzer.SentinelRegex`, the single sentinel
  grammar from ADR-0002. We expose a small helper
  `AgentOutcomeAnalyzer.MatchSentinels` so callers do not duplicate the
  pattern. Decision detection cares about `NEEDS_INPUT` and `BLOCKED` (the
  two *interruptive* sentinels); `DONE` and `NOOP` are post-run signals
  the existing analyzer already handles.
- **Resolved-vs-unresolved.** We reuse `ReviewDecisionParsing.LineHasFollowUpStream`:
  any subsequent `[orchestrator]`, `[supervisor]`, or `[user]` line resolves
  the sentinel. Mid-run, those streams come from typed
  `OrchestratorChatLog.Append` writes and from `AppendUserPromptToCliLog` on
  the user's reply. A *resolved* sentinel does not raise a banner.
- **Cadence.** Folded into `ProjectRunner.TickAsync` (the existing 5 s tick
  in `TaskRunnerService.ExecuteAsync`). We do not add a parallel timer.

### State

`ProjectRunner` carries an in-memory `_activePendingDecision: PendingDecision?`.
The record is populated when an unresolved sentinel is observed and cleared
when:

- the active job leaves `3-progress` (the runner already raises
  `JobTransitionService.OnJobMoved` and reconciles `_activeJobId`), or
- a follow-up appears in the buffer (the user replied via the banner or the
  orchestrator wrote one), or
- the run is killed.

### Read API

`GET /api/runner/{project}/pending-decisions` returns the active decision
sentinel(s) for the named project's running job. Shape:

```json
{
  "project": "agent-taskboard",
  "items": [
    {
      "jobId": "orchestrator-continuous-decision-visibility",
      "title": "Orchestrator continuous review and visible decision points",
      "kind": "needs-input",
      "reason": "which option do you want, A or B?",
      "detectedAt": "2026-05-06T10:21:45Z"
    }
  ]
}
```

`items` is empty when nothing is pending. The cardinality is at most 1
per project today (only one job runs in `3-progress` per project per ADR-0001),
but the surface is shaped as a list so a future "the orchestrator is also
asking" or "supervisor advisory" entry can join the same banner without an
API break.

### Reply

The user's reply goes through the existing `POST /api/jobs/{jobId}/continue`
endpoint with `mode: "steer"`. No new write surface. The banner mounts a
small textarea + "Reply" button that calls the same `JobService.continueJob`
the chat composer already calls. The orchestrator's [chat-log] write of the
user's message resolves the sentinel on the next tick.

## Why not a typed channel

The user's underlying need is *visibility*: a decision moment must stand
out for a human looking at the project view. That is a UI contract on top
of a detection surface. A typed channel would change neither the agent's
emission shape (it would still print sentinels in stdout regardless) nor
the user's banner. It would add:

- a per-CLI adapter to capture and forward the channel events,
- a serialization grammar to keep in sync with the stdout grammar,
- a process boundary for the CLI to emit those events without proper IPC,
- a persistence story for backend restarts mid-run.

All of that for the same end-state the stdout scanner gives us today,
in 5 s, with no driver changes.

## Cross-references

- **ADR-0002** - deterministic orchestration over prompt trust. The single
  sentinel grammar lives there; this scanner reuses it without forking.
- **ADR-0017** - supervisor as advisory layer. Decision detection is
  structural (sentinel match), not heuristic; it does not duplicate the
  supervisor's hard health-check loop.
- **ADR-0025** - three-stage review pipeline. Decision detection runs while
  the job sits in `3-progress`. The post-run path (`4-auto-review`,
  `ReviewDecisionOrchestrator`) is unchanged. The two existing pending
  surfaces stay distinct: `/api/projects/{name}/review-decisions-pending`
  (post-run, lane-scoped) and the new
  `/api/runner/{name}/pending-decisions` (live, runner-scoped).

## Implementation pointers

- `backend/Services/Runner/AgentOutcomeAnalyzer.cs::SentinelRegex` - the
  single sentinel grammar.
- `backend/Services/Runner/ReviewDecisionParsing.cs::LineHasFollowUpStream`
  - the resolved-vs-unresolved primitive.
- `backend/Services/Runner/ProjectRunner.cs::TickAsync` - the 5 s tick we
  piggyback on.
- `backend/Services/ICliExecutionService.cs::GetOutput` - the per-job
  buffer source.
- `backend/Endpoints/RunnerEndpoints.cs` - host for the new GET.
- `frontend/src/app/components/project-detail.ts` - host for the live
  banner; sits next to the existing post-run pending-decision banner with
  a different colour and an inline reply control.
