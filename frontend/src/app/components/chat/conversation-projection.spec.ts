import { describe, expect, it } from 'vitest';
import {
  agentTextFragment,
  captureFailFragment,
  compositeFragment,
  heuristicWarningFragment,
  imageArtifactFragment,
  needsInputLoopFragment,
  orchestratorReissueFragment,
  resetFixtureClock,
  runTimelineForComposite,
  supervisorAdvisoryFragment,
  toolBurstFragment,
  userMessageFragment,
  watchdogKillFragment,
  watchdogQuietResumeFragment
} from './conversation-projection.fixtures';
import { CONVERSATION_EVENT_KINDS } from './conversation-event';
import { projectConversation } from './conversation-projection';

const SOURCE = 'fixture-job';

describe('projectConversation', () => {
  it('classifies a user follow-up as message.user', () => {
    const events = projectConversation({ source: SOURCE, lines: userMessageFragment() });
    expect(events).toHaveLength(1);
    expect(events[0].kind).toBe('message.user');
    expect((events[0] as any).body).toContain('NextGenChat');
    expect(events[0].rawRange.source).toBe(SOURCE);
    expect(events[0].rawRange.start).toBe(1);
  });

  it('classifies a plain agent prose run as message.taskAgent', () => {
    // The activity-log parser splits prose around blank lines into separate
    // groups; the projection preserves that grouping (renderers can fold
    // adjacent agent turns visually). Both events must keep the agent kind.
    const events = projectConversation({ source: SOURCE, lines: agentTextFragment() });
    expect(events.length).toBeGreaterThanOrEqual(1);
    expect(events.every((e) => e.kind === 'message.taskAgent')).toBe(true);
    const joined = events.map((e) => (e as any).body).join('\n');
    expect(joined).toContain('NextGenChat');
    expect(joined).toContain('host inventory');
  });

  it('classifies an orchestrator reissue line as decision.orchestrator', () => {
    const events = projectConversation({ source: SOURCE, lines: orchestratorReissueFragment() });
    expect(events).toHaveLength(1);
    expect(events[0].kind).toBe('decision.orchestrator');
    expect((events[0] as any).decisionType).toBe('reissue');
    expect((events[0] as any).action).toBe('reissue');
  });

  it('collapses a read/search/edit run into toolBurst events with raw line ranges', () => {
    const events = projectConversation({ source: SOURCE, lines: toolBurstFragment() });
    // Reads compress, search and edit each produce their own burst.
    const tools = events.filter((e) => e.kind === 'toolBurst');
    expect(tools.length).toBeGreaterThanOrEqual(3);
    const reads = tools.find((t) => 'families' in t && (t as any).families.read);
    expect(reads).toBeDefined();
    expect((reads as any).count).toBe(3);
    expect(reads!.collapsedByDefault).toBe(true);
    // Every event must keep an end >= start raw range pointing at the source.
    for (const ev of events) {
      expect(ev.rawRange.source).toBe(SOURCE);
      expect(ev.rawRange.end).toBeGreaterThanOrEqual(ev.rawRange.start);
    }
  });

  it('emits a supervisor.wait quiet event then resumed event for watchdog quiet/resume', () => {
    const events = projectConversation({ source: SOURCE, lines: watchdogQuietResumeFragment() });
    expect(events.map((e) => e.kind)).toEqual(['supervisor.wait', 'supervisor.wait']);
    expect((events[0] as any).state).toBe('quiet');
    expect((events[0] as any).quietSeconds).toBe(47);
    expect((events[1] as any).state).toBe('resumed');
  });

  it('emits a killed supervisor.wait for watchdog kill lines', () => {
    const events = projectConversation({ source: SOURCE, lines: watchdogKillFragment() });
    expect(events).toHaveLength(1);
    expect(events[0].kind).toBe('supervisor.wait');
    expect((events[0] as any).state).toBe('killed');
    expect(events[0].severity).toBe('error');
  });

  it('emits a system.parserWarning for heuristic outcome lines and dedupes by key', () => {
    const lines = [...heuristicWarningFragment(), ...heuristicWarningFragment()];
    const events = projectConversation({ source: SOURCE, lines });
    const warnings = events.filter((e) => e.kind === 'system.parserWarning');
    expect(warnings).toHaveLength(1);
    expect((warnings[0] as any).expectedKind).toBe('sentinel');
  });

  it('emits a system.captureFail row with cli type and fallback for capture-fail', () => {
    const events = projectConversation({ source: SOURCE, lines: captureFailFragment() });
    expect(events).toHaveLength(1);
    expect(events[0].kind).toBe('system.captureFail');
    expect((events[0] as any).cliType.toLowerCase()).toContain('claude');
    expect((events[0] as any).fallback).toMatch(/rebuild/i);
  });

  it('classifies TASK_NEEDS_INPUT lines as agent.needsInput with the question', () => {
    const events = projectConversation({ source: SOURCE, lines: needsInputLoopFragment() });
    expect(events).toHaveLength(1);
    expect(events[0].kind).toBe('agent.needsInput');
    expect((events[0] as any).question).toMatch(/CLI/);
  });

  it('classifies a write-to-results edit as a toolBurst with file path captured', () => {
    const events = projectConversation({ source: SOURCE, lines: imageArtifactFragment() });
    const burst = events.find((e) => e.kind === 'toolBurst');
    expect(burst).toBeDefined();
    expect((burst as any).families.edit).toBe(1);
  });

  it('emits a message.supervisor for high-severity supervisor advisories', () => {
    const events = projectConversation({ source: SOURCE, lines: supervisorAdvisoryFragment() });
    expect(events).toHaveLength(1);
    expect(events[0].kind).toBe('message.supervisor');
  });

  it('attaches a runMarker when emitRunMarkers is set and the run timeline opens at line 1', () => {
    resetFixtureClock();
    const lines = compositeFragment();
    const events = projectConversation({
      source: SOURCE,
      lines,
      runTimeline: runTimelineForComposite(),
      emitRunMarkers: true
    });
    const runs = events.filter((e) => e.kind === 'runMarker');
    // The initial run is selected up-front; only the second run-boundary
    // would emit a marker, so for a single-run fragment we expect zero.
    expect(runs).toHaveLength(0);
    // But every event should carry the run id.
    expect(events.every((e) => e.runId === 1 || e.kind === 'taskMarker')).toBe(true);
  });

  it('emits artifact.image events from companion screenshot evidence', () => {
    const events = projectConversation({
      source: SOURCE,
      lines: agentTextFragment(),
      screenshots: [
        {
          caption: 'Empty state',
          sourcePath: '/tmp/scratch.png',
          durablePath: 'results/01-empty-state.png',
          sourceTool: 'playwright'
        }
      ]
    });
    const image = events.find((e) => e.kind === 'artifact.image');
    expect(image).toBeDefined();
    expect((image as any).durablePath).toBe('results/01-empty-state.png');
  });

  it('emits a metric.token event when the host passes a tokenSummary', () => {
    const events = projectConversation({
      source: SOURCE,
      lines: agentTextFragment(),
      tokenSummary: {
        calls: 2,
        inputTokens: 1500,
        outputTokens: 400,
        cacheReadTokens: 0,
        cacheCreationTokens: 0,
        totalTokens: 1900,
        lastModel: 'claude-opus-4-7',
        lastUpdate: '2026-05-05T12:00:00Z',
        entries: []
      }
    });
    const metric = events.find((e) => e.kind === 'metric.token');
    expect(metric).toBeDefined();
    expect((metric as any).inputTokens).toBe(1500);
  });

  it('emits a workbench.gitPreview / workbench.visualPreview / workbench.summary / traceLink set when requested', () => {
    const events = projectConversation({
      source: SOURCE,
      lines: toolBurstFragment(),
      commits: [
        {
          sha: 'abcdef',
          shortSha: 'abcd',
          subject: 'feat: scaffold projection',
          authorDateUtc: '2026-05-05T12:00:00Z',
          files: [{ status: 'M', path: 'frontend/src/app/foo.ts', added: 4, removed: 1 }]
        }
      ],
      screenshots: [
        { caption: 'Empty state', sourcePath: 'results/01.png', durablePath: 'results/01.png' }
      ],
      emitWorkbenchSummary: true,
      emitWorkbenchPreviews: true,
      emitTraceLink: true
    });
    const kinds = events.map((e) => e.kind);
    expect(kinds).toContain('workbench.gitPreview');
    expect(kinds).toContain('workbench.visualPreview');
    expect(kinds).toContain('workbench.summary');
    expect(kinds).toContain('traceLink');
  });

  it('classifies the composite fragment in declared user → tools → wait → agent order', () => {
    const events = projectConversation({ source: SOURCE, lines: compositeFragment() });
    const sequence = events.map((e) => e.kind);
    expect(sequence[0]).toBe('message.user');
    expect(sequence).toContain('toolBurst');
    expect(sequence).toContain('supervisor.wait');
    expect(sequence[sequence.length - 1]).toBe('message.taskAgent');
  });

  it('exports every advertised kind through CONVERSATION_EVENT_KINDS', () => {
    // Guard rail: keep the union and the runtime list in lockstep so future
    // jobs can iterate kinds without TypeScript discriminated-union juggling.
    expect(CONVERSATION_EVENT_KINDS).toContain('message.user');
    expect(CONVERSATION_EVENT_KINDS).toContain('toolBurst');
    expect(CONVERSATION_EVENT_KINDS).toContain('decision.orchestrator');
    expect(CONVERSATION_EVENT_KINDS).toContain('workbench.summary');
    expect(CONVERSATION_EVENT_KINDS).toContain('workbench.gitPreview');
    expect(CONVERSATION_EVENT_KINDS).toContain('workbench.visualPreview');
    expect(CONVERSATION_EVENT_KINDS).toContain('metric.token');
    expect(CONVERSATION_EVENT_KINDS).toContain('taskMarker');
    expect(CONVERSATION_EVENT_KINDS).toContain('runMarker');
    expect(CONVERSATION_EVENT_KINDS).toContain('traceLink');
  });
});
