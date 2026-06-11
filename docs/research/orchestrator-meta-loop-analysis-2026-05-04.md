# Multi-Loop Orchestrator Supervision - Analysis (2026-05-04)

Status: open analysis. Not yet implemented. Linked from [README.md](../../README.md) and [ROADMAP.md](../../ROADMAP.md).

## What this document is for

The user has identified a missing layer in the orchestration model. Today the app runs **two** loops:

1. The CLI agent's internal loop (Claude / Codex / Copilot / Gemini iterating on tools until done).
2. The orchestrator's job-pickup loop per project (pick next ready, start, observe, transition, repeat).

The user wants:

3. A **per-project meta-orchestrator loop** that watches loop &num;2: "What are you doing? Does it make sense? Are there problems?" Continuous, with its own protocol and traceability.
4. A higher-level **review monitor** for the running stable instance, run from outside on a much longer cadence (hours), producing periodic system-health reports.

Before any implementation, the conceptual problem of *loop-to-loop control* needs to be settled: how does a higher loop influence a lower one? Should it? With what authority and what guarantees? This document captures the analysis, recommends a strategy, lists the trade-offs and non-goals, and spins out the tasks that should be queued once the user signs off.

The implementation is **not** included here. The user will read this, edit it, then a separate task does the build.

## The current loop landscape

### Layer 0 - CLI agent loop (the worker)

Owned by the CLI vendor. The agent reads tools, plans, calls tools, and iterates until it emits a sentinel ([`[[TASK_DONE]]`](../contracts/agent-task.md), `[[TASK_BLOCKED:<reason>]]`, `[[TASK_NEEDS_INPUT:<reason>]]`, `[[TASK_NOOP]]`) or the process exits. The orchestrator does not control this inner loop directly - it can only start the process, observe stdout, and signal cancellation by killing it.

### Layer 1 - Orchestrator job-pickup loop (the manager)

Per project. Picks the next ready task, starts a CLI run, observes the run, applies the [outcome policy](../../backend/Services/Runner/RunOutcomePolicy.cs) when the run ends, transitions the task state, and loops back. Implemented in the backend, hosted as a per-project runner. The deterministic-arbitration triplet [`AgentOutcomeAnalyzer`](../../backend/Services/Runner/AgentOutcomeAnalyzer.cs) + `RunOutcomePolicy` + `OrchestratorChatLog` is the load-bearing logic; sentinels are authoritative when present, heuristics are surfaced as warnings.

This loop already has one feedback mechanism above it: the global orchestrator session (one Claude session per project booted at app start) which decides re-issues and intervention messages. That is *inside* the orchestrator's logic, not above it.

### What is missing

There is no continuous external supervisor that can ask, in real time, "should this run be cancelled?", "is the agent stuck?", "is quota almost burned?", "are we accumulating findings the orchestrator is ignoring?", or "is the current task aligned with what the user actually asked for?" Today such judgements happen at run-end (via the post-run policy) or are left to the human watching the activity log.

## What the user is asking for

### Layer 2 - Per-project meta-loop (the supervisor)

Runs per project, continuously. Observes Layer 1's state and recent activity. Asks a small fixed set of questions every tick. Surfaces concerns to the user. May (under controlled conditions) intervene: cancel the running CLI, pause job pickup, force-fail a stuck task. Maintains its own append-only protocol so the user can reconstruct what the supervisor saw and decided.

User framing: the supervisor should continuously ask what the lower loop is doing, whether that activity still makes sense, and whether any problems are visible.

### Layer 3 - System review monitor (the auditor)

Runs from outside the app, on stable, on an hours-to-days cadence. Reads the system as a whole: jobs across all projects, recent activity, recent commits, supervisor logs. Produces a periodic structured report: "after ten hours of running, here is what the system has done and here is what looks off." User framing: a slow external review loop that lets the user inspect system behavior after a long unattended run.

This layer is operationally distinct: not part of the app's runtime, driven externally (Claude Code spawned by the user, or a scheduled task).

## The core conceptual problem: how does a higher loop control a lower one?

Three positions sit on a continuum from observe-only to fully pre-emptive.

### Option A - Decoupled observation

Meta-loop observes only. Surfaces findings as banners, suggestions, or queued review tasks. The user is the only actor that can intervene. Layer 1 never receives signals from Layer 2.

- Pro: simplest. No coupling between loops. Layer 1 stays predictable. No race conditions.
- Pro: the supervisor cannot create new bugs by intervening.
- Con: every intervention requires human latency. Stuck runs burn quota until the human notices.
- Con: the user gets tired of being the only intervention path; "supervisor" becomes "yet another notifier".

### Option B - Cooperative signalling

Meta-loop writes advisory signals to a shared, typed channel (an append-only log + a small in-memory state object). Layer 1 reads the signal at well-defined tick points (between runs, between tool calls if instrumented) and decides whether to honour it. The lower loop owns its own lifecycle; signals are advice, not commands.

- Pro: each loop is the authority over its own state. State machine stays consistent.
- Pro: the orchestrator can ignore signals it cannot safely honour, with reason logged.
- Con: requires Layer 1 code to opt in at every interesting point. Latency between signal and action is one tick.
- Con: needs care to avoid feedback loops (supervisor's own signals ending up in its next observation).

### Option C - Pre-emptive control via handles

Meta-loop holds direct handles to Layer 1's running things (the CLI process, the job state record, the runner). It can directly: cancel a CLI run, pause job pickup, force-fail a task, inject a "stop and ask the user" framing. No cooperation required from Layer 1.

- Pro: fast, decisive, can stop bad behaviour mid-tool-call.
- Con: race conditions. The CLI may be mid-write when killed; logs split. Three-way authority over the same process (CLI, orchestrator, supervisor) makes failure analysis hard.
- Con: encourages the supervisor to grow into a second orchestrator. The deterministic policy in Layer 1 was hard-won; a parallel control path can undermine it.

### Recommendation: Option B as default, with a small Option C primitive for emergencies

- Default behaviour: cooperative signalling. Meta-loop writes typed advisories ("agent has emitted no log line for 10 minutes", "quota at 5 % remaining", "diff diverges from prompt scope") to a shared channel. Layer 1 reads at safe tick points and decides.
- Emergency primitives: a small, explicit set of pre-emptive actions, each with mandatory typed reason and full log entry on both sides:
  - `cancelRun(jobId, reason)` - kill the CLI process for this run.
  - `pausePickup(reason)` - orchestrator stops picking new tasks until resumed.
  - `forceFail(jobId, reason)` - mark a task failed without further retry.
  - `resume()` - lift a pause.

  These exist for the "agent is clearly broken / costing real money / about to do harm" case. Each invocation must justify itself in the log, and the user-visible activity feed shows them as a separate participant.
- The human stays in the kill-switch role for non-emergency interventions. Supervisor is *advice-first*, *force-rare*.

Rationale: the existing orchestrator's value is its determinism. A cooperative-signal model preserves that. The emergency primitives give us the "stop the burning machine" capability without adopting it as the default.

## Communication contract (sketch)

```
MetaLoop -> Orchestrator (Layer 1):
  observe()                           // read-only: current state, recent activity
  advise(severity, topic, message)    // typed advisory; appended to shared log
  cancelRun(jobId, reason)            // emergency
  pausePickup(reason)                 // emergency
  forceFail(jobId, reason)            // emergency
  resume()                            // lift pause

Orchestrator -> MetaLoop (passive):
  // Orchestrator does not call the meta-loop.
  // The meta-loop polls observe() each tick.
  // Orchestrator writes its own decisions into the shared log;
  // the meta-loop reads from the same log on its next observation.

State the orchestrator exposes via observe():
  per project: runner status, current jobId, current run state, last-progress-ts,
               quota window, last N orchestrator decisions, last N agent
               output samples, error counters.
```

Shared log per project:

```
logs/meta/<project>/observations.jsonl   // what the meta-loop saw, structured
logs/meta/<project>/interventions.jsonl  // what the meta-loop did
logs/meta/<project>/reasoning.md         // narrative rollup, periodic
```

The orchestrator's existing `cli-output.log` and `OrchestratorChatLog` continue to write the same way; the meta-loop's outputs live alongside, never inside, those files.

## Per-project meta-loop: execution model

Three options for *how* the meta-loop actually runs.

### Option 1 - In-process supervisor

Hosted service inside the backend. One tick per project per N seconds (e.g. 10 s). Reads in-memory state, writes the per-project log.

- Pro: simplest deployment. No extra processes. Cheap.
- Con: shares fate with the orchestrator. If the backend hangs, both freeze.
- Best for: hard health checks (process alive, progress timestamp, quota counters, error bursts).

### Option 2 - Sidecar process

Separate process. Reads the same on-disk state, writes its own log.

- Pro: isolated. Backend hang does not freeze the supervisor. Survives a partial backend crash.
- Con: more deployment complexity. Crosses the in-memory state boundary.
- Best for: when Option 1 proves unreliable in practice; not needed up front.

### Option 3 - CLI-driven soft reasoning

The meta-loop *is* a Claude (or other) session per project, prompted with project state and recent log, asked to flag anomalies. Cadence: every 5-10 minutes.

- Pro: rich reasoning. Catches subtle issues a checklist misses.
- Pro: produces narrative findings the user can read.
- Con: token cost. Latency. Cannot react fast to a hard hang.

### Recommendation: hybrid (Option 1 + Option 3), revisit Option 2 only if needed

- **Hard checks** - in-process, every 10 s. Looks for: process-not-progressing, quota near zero, error-burst, log silence beyond threshold, tool-call repetition. Cheap, fast, deterministic.
- **Soft reasoning** - CLI-driven, every 5-10 min per project. Reads project state + recent log + recent diffs, writes a structured finding to `observations.jsonl` and a narrative paragraph to `reasoning.md`. Severity-tagged.
- **Emergency primitives** - available to the in-process layer; the CLI-driven layer can suggest interventions, the in-process layer is what actually invokes them.

Token budget: 12 calls / hour / project for Option 3 at the slow cadence is the upper bound. With ten watched projects that is 120 calls / hour. Worth making the cadence configurable per project; default to 10 minutes; let high-traffic or high-risk projects move to 5.

## Layer 3 - System review monitor for stable

Conceptually different from Layer 2. This watches the *whole stable instance* on a slow cadence and produces a written review the user can read at their leisure.

- Cadence: every 4 / 8 / 24 hours, configurable.
- Driver: external (Claude Code spawned by the user from outside, or a scheduled task on the host machine). Not part of the app's runtime.
- Inputs: the app's job folders, activity logs, supervisor logs, recent commits.
- Output: `logs/system-review/<date>-<time>.md` - structured review with sections for:
  - what ran, with success/fail counts;
  - findings the supervisor produced and whether they were addressed;
  - quota usage and projected burn;
  - anomalies (tasks that took unusually long, repeated failures, supervisor interventions that fired);
  - "is the system behaving correctly?" verdict in plain prose.

This is the user's "review instance" - a third loop that watches both the orchestrator and the supervisor over time.

## Traceability requirement

User wants "fortwährendes Protokoll". Concretely:

- Append-only, structured logs per project per loop layer (paths above).
- A "Supervisor" panel on the project page showing current state, recent advisories, recent interventions, and a link to the full reasoning rollup.
- The Activity Log gets a fourth participant: alongside `You`, the agent, and `[orchestrator]`, add `[supervisor]`. Same render contract as the orchestrator participant.
- Every emergency-primitive invocation appears in both the supervisor log and the orchestrator log so the cause-and-effect is reconstructable from either side.

## Open conceptual problems

These need explicit answers before or during implementation. Several have no obvious right answer.

1. **Three-way authority over a CLI process.** The CLI itself, the orchestrator's stop endpoint, and the supervisor's `cancelRun` can all kill the run. Whose timestamp wins in the log when two race? Recommendation: every kill records who-tried-first, who-actually-killed; the orchestrator is the last writer for state transitions to keep the state machine single-authority.
2. **Quantitative definition of "stuck".** No log line in N seconds? Same tool repeated K times? Prompt growth without diff growth? Each candidate definition has false positives. The hard-check layer must err on the side of *advise*, not *intervene*.
3. **Feedback loop avoidance.** Supervisor advisories appear in the shared log. If the supervisor reads its own advisories on the next tick, it can spiral. Fix: tag every event with `source` (`agent`, `orchestrator`, `supervisor`); supervisor filters out `source: supervisor` from its observation input.
4. **Token cost.** Soft reasoning at 5-10 minute cadence across many projects accumulates. Budget must be explicit, configurable per project, and surface the burn in the UI.
5. **What if the supervisor hangs?** Same risks as the orchestrator hanging, regressed one level. Watchdog on the watchdog. At some point we stop adding layers and accept that the *next* outer loop is the human or Layer 3. Acceptable: in-process Option 1 has a heartbeat written to disk; Layer 3's review notices a missing heartbeat.
6. **UI surface choice.** Is the supervisor a panel on the project page? An indicator on the orchestrator card? A new top-level "Health" view? Recommendation: a panel on the project page (alongside the existing orchestrator session card), plus a small badge in the project list when interventions are active.
7. **What happens to a paused project?** If `pausePickup` fires, the user sees what? Resume how? The pause itself becomes a kind of stuck state if the user does not notice. Recommendation: pauses auto-expire after a TTL with a default of 30 minutes; after expiry the orchestrator resumes and writes a clear entry to the chat log.
8. **Stable vs dev divergence.** The dev checkout will have the supervisor first. Stable lags. The user runs stable today; the meta-loop work happens here in dev. Layer 3 (review monitor) can target stable from outside *before* Layer 2 ships, and is therefore the lowest-risk first deliverable.
9. **Should the supervisor see the diff?** Reading the diff dramatically improves soft reasoning quality but adds tokens and risk (large diffs blow context). Recommendation: yes for soft reasoning, yes only when diff is below a configurable size, fall back to a summary when above.
10. **Per-project vs global.** The meta-loop is per project. But cross-project signals (quota burn, error patterns) need a global view. Layer 3 covers this; the per-project supervisor stays scoped.

## Recommended task spinout

Once the user has read and edited this analysis, these are the queued tasks. Numbered for reference; ordered roughly by dependency.

1. **Define the supervisor communication contract** - types, log shape, signal severity levels. Dependency for everything else. Pure design; lands as a markdown spec under `docs/`.
2. **Implement Layer 3 first (system review monitor)** - lowest risk, highest value-per-effort, immediately useful on stable. Stand-alone Claude Code invocation that reads stable's state and writes a review file. No app code changes required.
3. **Build read-only observation primitives in the backend** - the `observe()` API. Surface current state, recent activity, quota, last-progress-ts, error counters. Read-only; cannot break anything in Layer 1.
4. **Build emergency primitives** - `cancelRun`, `pausePickup`, `forceFail`, `resume`, with reason logging. Wired into the orchestrator's existing stop / state-machine paths so there is exactly one cancel implementation, not two.
5. **Implement Option 1 hard health checks** - in-process, every 10 s, advisory-only at first (no automatic cancel). Writes to `observations.jsonl`. Validates the contract end-to-end.
6. **UI: Supervisor panel on the project page** - alongside the orchestrator session card. Renders recent advisories and interventions. Lets the user manually trigger emergency primitives.
7. **Implement Option 3 soft reasoning** - CLI-driven, every 5-10 min per project. Configurable cadence and budget. Writes findings to the same log.
8. **Activity-log integration** - `[supervisor]` as a fourth participant alongside `You`, the agent, and `[orchestrator]`.
9. **Auto-intervention policy (final phase)** - promote selected hard-check advisories from advisory to automatic emergency-primitive invocation, gated by a per-project setting and severity threshold. This is the riskiest step; do it last when the rest is stable.
10. **AGENTS.md update** - document the three-layer model in the orchestration philosophy section.

## Tradeoffs and non-goals

- **Not** building a generic agent-supervision framework. Stay scoped to this app's orchestrator, this app's CLIs.
- **Not** duplicating Layer 1's deterministic policy. Supervisor is advisory; the orchestrator's `RunOutcomePolicy` stays authoritative for routine outcomes.
- **Not** pursuing fully autonomous interventions for high-severity actions in the first cuts. Human stays in the loop until the auto-intervention policy proves itself.
- **Not** running Layer 3 inside the app. It is deliberately external so it survives any failure mode of the app itself.
- **Not** introducing a fourth layer above Layer 3 right now. Acceptable risk: the user is the next outer loop above the system review monitor.

## Open questions for the user before implementation

1. Token budget - what is the acceptable upper bound for soft-reasoning calls per hour across all projects? Default suggestion: 60 (one project per minute on the slowest cadence).
2. Default cadence for hard checks - 10 s feels right but is it acceptable load? Lower bound 5 s, upper bound 30 s.
3. Pause TTL - 30 minutes default reasonable, or longer / shorter?
4. Auto-intervention - should auto-cancel of clearly stuck runs ever be enabled, or always require human confirmation? Recommend leaving it gated and disabled-by-default at first.
5. UI placement - supervisor panel on project page (recommended), or a new top-level Health view?
6. Layer 3 cadence - 4 h / 8 h / 24 h default? User's mention of "after ten hours" suggests something in the 4-8 h band.
7. Naming - "Supervisor", "Watcher", "Sentinel", "Meta-Orchestrator"? Recommend "Supervisor" because it matches the relationship (watches, sometimes intervenes) without implying it is just observing.
