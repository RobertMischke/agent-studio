/**
 * Fixture fragments for the next-gen chat projection.
 *
 * These are not full job logs. They are small, deterministic Activity Log
 * snippets distilled from the real fixtures listed in
 * `docs/mockups/chat-window-next-gen/activity-log-edge-cases.md` so the
 * projection's classification can be unit tested without dragging an entire
 * job folder into the test runner.
 *
 * Each helper returns plain `CliOutputLine[]` so the projection can be fed
 * the same way a host would feed it.
 */

import type { CliOutputLine, RunRecord, RunTimeline } from '../../models/job.model';

let TS_COUNTER = 0;
function ts(offsetSec = 0): string {
  // Anchor everything at 2026-05-05T12:00:00Z and walk forward; tests can
  // assert on timestamp ordering without flake.
  const base = Date.UTC(2026, 4, 5, 12, 0, 0);
  return new Date(base + (offsetSec + (TS_COUNTER += 1)) * 1000).toISOString();
}

function line(text: string, stream: string = 'stdout'): CliOutputLine {
  return { timestamp: ts(), stream, text };
}

export function resetFixtureClock(): void {
  TS_COUNTER = 0;
}

/** User asks a question. */
export function userMessageFragment(): CliOutputLine[] {
  resetFixtureClock();
  return [line('Please add a feature flag for NextGenChat.', 'user')];
}

/** A run of read / search / edit calls — the v6 "tool burst" canonical case. */
export function toolBurstFragment(): CliOutputLine[] {
  resetFixtureClock();
  return [
    line('* Read prompt.md'),
    line('  | prompt.md'),
    line('* Read status.md'),
    line('  | status.md'),
    line('* Read activity-log.parser.ts'),
    line('  | frontend/src/app/components/activity-log.parser.ts'),
    line('* Search "needsInput"'),
    line('  | needle'),
    line('* Edit feature-flags.service.ts'),
    line('  | feature-flags.service.ts')
  ];
}

/** A standalone agent prose turn (no tool noise). */
export function agentTextFragment(): CliOutputLine[] {
  resetFixtureClock();
  return [
    line('I will add a Frontend:NextGenChat flag and the projection scaffold next.'),
    line(''),
    line('After that the host inventory document follows.')
  ];
}

/** Orchestrator decides to reissue the task. */
export function orchestratorReissueFragment(): CliOutputLine[] {
  resetFixtureClock();
  return [line('[reissue] retrying because evidence was incomplete', 'orchestrator')];
}

/** Watchdog detects a quiet window then notes the agent resumed. */
export function watchdogQuietResumeFragment(): CliOutputLine[] {
  resetFixtureClock();
  return [
    line('[watchdog] Agent has been quiet for 47s', 'orchestrator'),
    line('[watchdog] Agent resumed streaming', 'orchestrator')
  ];
}

/** Watchdog kills the agent after a long silence. */
export function watchdogKillFragment(): CliOutputLine[] {
  resetFixtureClock();
  return [line('[watchdog] Killed after 600s of silence', 'orchestrator')];
}

/** Heuristic: orchestrator could not classify the agent reply. */
export function heuristicWarningFragment(): CliOutputLine[] {
  resetFixtureClock();
  return [
    line("[heuristic] Could not classify the agent's reply; defaulting to noop", 'orchestrator')
  ];
}

/** Capture-fail: no claude session id was harvested for this run. */
export function captureFailFragment(): CliOutputLine[] {
  resetFixtureClock();
  return [
    line('[capture-fail] No claude session id from claude this run; next follow-up will rebuild from disk', 'orchestrator')
  ];
}

/** Agent emits a NEEDS_INPUT sentinel that the orchestrator picks up. */
export function needsInputLoopFragment(): CliOutputLine[] {
  resetFixtureClock();
  return [
    line('[[TASK_NEEDS_INPUT: which CLI should I target for the recovery test?]]', 'orchestrator')
  ];
}

/** Image artefact: agent attaches a screenshot path. */
export function imageArtifactFragment(): CliOutputLine[] {
  resetFixtureClock();
  return [
    line('* Write results/01-empty-state.png'),
    line('  | results/01-empty-state.png')
  ];
}

/** Supervisor advisory row at high severity. */
export function supervisorAdvisoryFragment(): CliOutputLine[] {
  resetFixtureClock();
  return [line('Job is approaching its retry budget (high)', 'supervisor')];
}

/**
 * Watchdog wait loop: agent goes quiet, watchdog repeats the warning, the
 * agent eventually resumes streaming. This is the v6 "wait loop" canonical
 * case from `activity-log-edge-cases.md`.
 */
export function waitLoopFragment(): CliOutputLine[] {
  resetFixtureClock();
  return [
    line('[watchdog] Agent has been quiet for 30s', 'orchestrator'),
    line('[watchdog] Still silent at 60s', 'orchestrator'),
    line('[watchdog] Still silent at 120s', 'orchestrator'),
    line('[watchdog] Agent resumed streaming', 'orchestrator')
  ];
}

/**
 * Token spike: orchestrator and supporting-agent calls land near each other
 * with conspicuously high usage. The fixture exposes the lines plus an
 * accompanying `JobTokenSummary` companion the projection can read.
 */
export function tokenSpikeFragment(): CliOutputLine[] {
  resetFixtureClock();
  return [
    line('Continue with the long synthesis pass.', 'user'),
    line('Synthesizing the meta-cycle report...'),
    line('  | walking 30k lines of evidence')
  ];
}

export function tokenSpikeSummary(): import('../../models/job.model').JobTokenSummary {
  return {
    calls: 4,
    inputTokens: 280_000,
    outputTokens: 14_500,
    cacheReadTokens: 9_400,
    cacheCreationTokens: 0,
    totalTokens: 303_900,
    lastModel: 'claude-opus-4-7',
    lastUpdate: '2026-05-05T12:05:00Z',
    entries: []
  };
}

/**
 * Schema drift: orchestrator (or meta-cycle hosted service) reports that a
 * structured Markdown / JSON report could not be parsed. The projection
 * raises a `system.schemaDrift` event, not a generic parser warning.
 */
export function schemaDriftFragment(): CliOutputLine[] {
  resetFixtureClock();
  return [
    line('[schema-drift] Failed to parse expected MetaCycleReport.json: missing recommendations[]', 'orchestrator')
  ];
}

/**
 * A failing test followed by a passing retry. This stresses the tool-burst
 * `tests` aggregate (one failure, then one pass) plus the failure flag
 * surfacing into the workbench summary.
 */
export function testFailRetryFragment(): CliOutputLine[] {
  resetFixtureClock();
  return [
    line('* Run npx playwright test perf-frontend.spec.ts (shell)'),
    line('  | running playwright tests'),
    line('x Run npx playwright test perf-frontend.spec.ts (shell): exited with error 1'),
    line('  | grouped jobs poll took 11521 ms', 'stderr'),
    line('* Run npx playwright test perf-frontend.spec.ts (shell)'),
    line('  | rerunning after fix'),
    line('* Run npx playwright test perf-frontend.spec.ts (shell)'),
    line('  | passed in 320ms')
  ];
}

/** Composite sample mixing user → tools → agent with a watchdog quiet event. */
export function compositeFragment(): CliOutputLine[] {
  resetFixtureClock();
  return [
    line('Continue the implementation', 'user'),
    line('* Read AGENTS.md'),
    line('  | AGENTS.md'),
    line('* Edit feature-flags.service.ts'),
    line('  | feature-flags.service.ts'),
    line('[watchdog] Agent has been quiet for 30s', 'orchestrator'),
    line('Adding the projection module now.'),
    line('')
  ];
}

/**
 * A minimal run timeline matching the composite fragment so projection tests
 * can assert that `runMarker` events fire on transitions.
 */
export function runTimelineForComposite(): RunTimeline {
  const run: RunRecord = {
    index: 1,
    intent: 'continue',
    startedAt: '2026-05-05T12:00:00Z',
    endedAt: '2026-05-05T12:02:00Z',
    status: 'completed',
    cli: 'claude',
    exitCode: 0,
    durationSeconds: 120,
    inputSessionId: null,
    capturedSessionId: 'sess-abc',
    resumed: false,
    reason: null,
    userFollowup: 'Continue the implementation',
    lineStart: 1,
    lineEnd: 7,
    headShaBefore: null,
    headShaAfter: null
  };
  return {
    runCount: 1,
    firstStartedAt: run.startedAt,
    lastActivityAt: run.endedAt,
    hasActiveRun: false,
    runs: [run]
  };
}
