import { describe, expect, it } from 'vitest';
import {
  agentTextFragment,
  captureFailFragment,
  compositeFragment,
  heuristicWarningFragment,
  imageArtifactFragment,
  modelSwitchFragment,
  needsInputLoopFragment,
  orchestratorReissueFragment,
  resetFixtureClock,
  runTimelineForComposite,
  runTimelineForModelSwitch,
  schemaDriftFragment,
  taskboardStartedFragment,
  supervisorAdvisoryFragment,
  testFailRetryFragment,
  tokenSpikeFragment,
  tokenSpikeSummary,
  toolBurstFragment,
  userMessageFragment,
  waitLoopFragment,
  watchdogKillFragment,
  watchdogQuietResumeFragment
} from './conversation-projection.fixtures';
import { CONVERSATION_EVENT_KINDS } from './conversation-event';
import type { ConversationEvent, RawLineRange } from './conversation-event';
import { projectConversation } from './conversation-projection';
import type { CliOutputLine } from '../../models/task.model';

const SOURCE = 'fixture-job';

function line(text: string, stream = 'stdout', timestamp = '2026-04-26T12:00:00.000Z'): CliOutputLine {
  return { timestamp, stream, text };
}

interface EventProbe {
  action?: string;
  actorCounts: { user: number; taskAgent: number };
  aggregate: {
    state?: string;
    runCount?: number;
    latestRunStatus?: string;
    totalDurationSeconds?: number;
    totalInputTokens?: number;
    totalOutputTokens?: number;
    commitCount?: number;
    filesChanged?: number;
    screenshotCount?: number;
    toolCallCount?: number;
    toolFailureCount?: number;
    retryWarningCount?: number;
    latestResult?: string;
  };
  artifacts: readonly string[];
  body?: string;
  cliType?: string;
  collapsedByDefault: boolean;
  count: number;
  commands: readonly {
    command: string;
    exitCode: number | null;
    output: string;
    outputLineCount: number;
    outputTruncated: boolean;
    hits?: readonly { path: string; line: number; text: string }[];
  }[];
  category?: string;
  decisionType?: string;
  durablePath?: string;
  expectedKind?: string;
  expectedSchema?: string;
  explanation?: string;
  fallback?: string;
  failures?: number;
  label?: string;
  nextStep?: string;
  families: { edit?: number; read?: number; search?: number };
  files: readonly string[];
  headline?: string;
  inputTokens?: number;
  link: { range: RawLineRange };
  quietSeconds?: number;
  question?: string;
  rawLink?: { range: RawLineRange };
  rawRange: RawLineRange;
  runStats: { runCount: number; completedCount: number };
  severity?: string;
  state?: string;
  tests: readonly { status: string }[];
  tokenTotals: { inputTokens: number };
  toolDensity: { total: number };
  traceLinks: readonly { range: RawLineRange }[];
  warningCounts: {
    captureFails: number;
    parserWarnings: number;
    schemaDrifts: number;
    watchdogQuiet: number;
  };
}

function probe(event: ConversationEvent | undefined): EventProbe {
  expect(event).toBeDefined();
  return event as unknown as EventProbe;
}

describe('projectConversation', () => {
  it('classifies a user follow-up as message.user', () => {
    const events = projectConversation({ source: SOURCE, lines: userMessageFragment() });
    expect(events).toHaveLength(1);
    expect(events[0].kind).toBe('message.user');
    expect(probe(events[0]).body).toContain('NextGenChat');
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
    const joined = events.map((e) => probe(e).body).join('\n');
    expect(joined).toContain('NextGenChat');
    expect(joined).toContain('host inventory');
  });

  it('classifies an orchestrator reissue line as decision.orchestrator', () => {
    const events = projectConversation({ source: SOURCE, lines: orchestratorReissueFragment() });
    expect(events).toHaveLength(1);
    expect(events[0].kind).toBe('decision.orchestrator');
    expect(probe(events[0]).decisionType).toBe('reissue');
    expect(probe(events[0]).action).toBe('reissue');
  });

  it('collapses a contiguous read/search/edit run into a single multi-family toolBurst', () => {
    const events = projectConversation({ source: SOURCE, lines: toolBurstFragment() });
    // The whole tool-heavy fragment must surface as one dense row, not a wall
    // of chips. Family counts and total stay accurate so renderers can show
    // "12 reads · 3 searches · 4 edits" inside that single row.
    const tools = events.filter((e) => e.kind === 'toolBurst');
    expect(tools).toHaveLength(1);
    const burst = probe(tools[0]);
    expect(burst.count).toBe(5);
    expect(burst.families.read).toBe(3);
    expect(burst.families.search).toBe(1);
    expect(burst.families.edit).toBe(1);
    expect(burst.failures).toBe(0);
    expect(burst.collapsedByDefault).toBe(true);
    expect(burst.rawRange.source).toBe(SOURCE);
    // Range spans the whole tool-heavy stretch so Trace can jump back to it.
    expect(burst.rawRange.start).toBe(1);
    expect(burst.rawRange.end).toBeGreaterThan(burst.rawRange.start);
    for (const ev of events) {
      expect(ev.rawRange.source).toBe(SOURCE);
      expect(ev.rawRange.end).toBeGreaterThanOrEqual(ev.rawRange.start);
    }
  });

  it('projects Codex command executions as expandable command output inside the tool burst', () => {
    const events = projectConversation({
      source: SOURCE,
      lines: [
        line('{"type":"item.completed","item":{"id":"cmd_1","type":"command_execution","command":"rg -n \\"needle\\" frontend/src/app","aggregated_output":"frontend/src/app/a.ts:12:const needle = true;\\nfrontend/src/app/b.ts:8:needle();","exit_code":0,"status":"completed"}}')
      ]
    });
    const burst = probe(events.find((e) => e.kind === 'toolBurst'));
    expect(burst.commands).toHaveLength(1);
    expect(burst.commands[0].command).toContain('rg -n');
    expect(burst.commands[0].exitCode).toBe(0);
    expect(burst.commands[0].output).toContain('frontend/src/app/a.ts:12');
    expect(burst.commands[0].hits).toHaveLength(2);
    expect(burst.commands[0].hits?.[0].path).toBe('frontend/src/app/a.ts');
    expect(burst.commands[0].hits?.[0].line).toBe(12);
  });

  it('turns Codex tool-router errors into typed parser warning rows', () => {
    const events = projectConversation({
      source: SOURCE,
      lines: [
        line('ERROR codex_core::tools::router: error=Exit code: 1', 'stderr')
      ]
    });
    expect(events).toHaveLength(1);
    expect(events[0].kind).toBe('system.parserWarning');
    expect(probe(events[0]).expectedKind).toBe('tool-result');
  });

  it('renders codex silent-completion as a typed status event instead of raw bracket text', () => {
    const events = projectConversation({
      source: SOURCE,
      lines: [
        line('[codex-silent-completion] Codex stopped after final tool call (silence=64s >= 60s)', 'orchestrator')
      ]
    });
    expect(events).toHaveLength(1);
    expect(events[0].kind).toBe('system.status');
    expect(probe(events[0]).category).toBe('codex-silent-completion');
    expect(probe(events[0]).severity).toBe('warn');
  });

  it('emits a supervisor.wait quiet event then resumed event for watchdog quiet/resume', () => {
    const events = projectConversation({ source: SOURCE, lines: watchdogQuietResumeFragment() });
    expect(events.map((e) => e.kind)).toEqual(['supervisor.wait', 'supervisor.wait']);
    expect(probe(events[0]).state).toBe('quiet');
    expect(probe(events[0]).quietSeconds).toBe(47);
    expect(probe(events[1]).state).toBe('resumed');
  });

  it('emits a killed supervisor.wait for watchdog kill lines', () => {
    const events = projectConversation({ source: SOURCE, lines: watchdogKillFragment() });
    expect(events).toHaveLength(1);
    expect(events[0].kind).toBe('supervisor.wait');
    expect(probe(events[0]).state).toBe('killed');
    expect(events[0].severity).toBe('error');
  });

  it('emits a system.parserWarning for heuristic outcome lines and dedupes by key', () => {
    const lines = [...heuristicWarningFragment(), ...heuristicWarningFragment()];
    const events = projectConversation({ source: SOURCE, lines });
    const warnings = events.filter((e) => e.kind === 'system.parserWarning');
    expect(warnings).toHaveLength(1);
    expect(probe(warnings[0]).expectedKind).toBe('sentinel');
  });

  it('emits a system.captureFail row with cli type and fallback for capture-fail', () => {
    const events = projectConversation({ source: SOURCE, lines: captureFailFragment() });
    expect(events).toHaveLength(1);
    expect(events[0].kind).toBe('system.captureFail');
    expect(probe(events[0]).cliType?.toLowerCase()).toContain('claude');
    expect(probe(events[0]).fallback).toMatch(/rebuild/i);
  });

  it('classifies TASK_NEEDS_INPUT lines as agent.needsInput with the question', () => {
    const events = projectConversation({ source: SOURCE, lines: needsInputLoopFragment() });
    expect(events).toHaveLength(1);
    expect(events[0].kind).toBe('agent.needsInput');
    expect(probe(events[0]).question).toMatch(/CLI/);
  });

  it('parses a standalone agent [[TASK_DONE]] sentinel into a result status chip', () => {
    const events = projectConversation({ source: SOURCE, lines: [line('[[TASK_DONE]]')] });
    expect(events).toHaveLength(1);
    expect(events[0].kind).toBe('system.status');
    expect(probe(events[0]).category).toBe('result');
    expect(probe(events[0]).severity).toBe('info');
    expect(probe(events[0]).label).toMatch(/complete/i);
  });

  it('keeps agent prose and strips the sentinel when both share a line', () => {
    const events = projectConversation({ source: SOURCE, lines: [line('All checks pass. [[TASK_DONE]]')] });
    const msg = events.find((e) => e.kind === 'message.taskAgent');
    const status = events.find((e) => e.kind === 'system.status');
    expect(msg).toBeDefined();
    expect(probe(msg).body).toContain('All checks pass.');
    expect(probe(msg).body).not.toContain('[[TASK_DONE]]');
    expect(status).toBeDefined();
    expect(probe(status).category).toBe('result');
    // Nothing leaks the raw bracket text into any event body.
    for (const ev of events) {
      expect(probe(ev).body ?? '').not.toContain('[[TASK_DONE]]');
    }
  });

  it('parses [[TASK_BLOCKED:reason]] into an error result carrying the reason', () => {
    const events = projectConversation({ source: SOURCE, lines: [line('[[TASK_BLOCKED: backend is down]]')] });
    expect(events).toHaveLength(1);
    expect(events[0].kind).toBe('system.status');
    expect(probe(events[0]).category).toBe('result');
    expect(probe(events[0]).severity).toBe('error');
    expect(probe(events[0]).explanation).toContain('backend is down');
  });

  it('parses [[TASK_NOOP]] into an informational "no action" result chip', () => {
    const events = projectConversation({ source: SOURCE, lines: [line('[[TASK_NOOP]]')] });
    expect(events).toHaveLength(1);
    expect(events[0].kind).toBe('system.status');
    expect(probe(events[0]).category).toBe('result');
    expect(probe(events[0]).label).toMatch(/no action/i);
  });

  it('parses an agent-stream [[TASK_NEEDS_INPUT:...]] into agent.needsInput', () => {
    const events = projectConversation({ source: SOURCE, lines: [line('[[TASK_NEEDS_INPUT: which port should I use?]]')] });
    expect(events).toHaveLength(1);
    expect(events[0].kind).toBe('agent.needsInput');
    expect(probe(events[0]).question).toMatch(/port/i);
  });

  it('classifies a write-to-results edit as a toolBurst with file path captured', () => {
    const events = projectConversation({ source: SOURCE, lines: imageArtifactFragment() });
    const burst = events.find((e) => e.kind === 'toolBurst');
    expect(burst).toBeDefined();
    expect(probe(burst).families?.edit).toBe(1);
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

  it('reads the per-run model from a [taskboard] Started marker and drops the marker line', () => {
    const events = projectConversation({
      source: SOURCE,
      lines: taskboardStartedFragment('claude-opus-4-8')
    });
    // The marker is run bookkeeping, not a chat row: only the agent turn
    // survives, and it carries the model the marker announced.
    expect(events).toHaveLength(1);
    expect(events[0].kind).toBe('message.taskAgent');
    expect(events[0].model).toBe('claude-opus-4-8');
    // No event should echo the raw [taskboard] marker text.
    expect(events.some((e) => probe(e).body?.includes('[taskboard]'))).toBe(false);
  });

  it('attributes the model per output across a mid-task model switch', () => {
    const events = projectConversation({
      source: SOURCE,
      lines: modelSwitchFragment('gpt-5-codex', 'claude-opus-4-7'),
      runTimeline: runTimelineForModelSwitch(),
      emitRunMarkers: true
    });
    const agents = events.filter((e) => e.kind === 'message.taskAgent');
    // Each run's reply keeps its OWN run model — never one global value.
    expect(agents.map((e) => e.model)).toEqual(['gpt-5-codex', 'claude-opus-4-7']);
    // The second run-boundary emits a runMarker carrying the switched model.
    const runs = events.filter((e) => e.kind === 'runMarker');
    expect(runs).toHaveLength(1);
    expect(runs[0].model).toBe('claude-opus-4-7');
  });

  it('does not fabricate a model for orchestrator or user rows the log never names', () => {
    const events = projectConversation({
      source: SOURCE,
      lines: [
        ...taskboardStartedFragment('claude-opus-4-8'),
        ...orchestratorReissueFragment(),
        ...userMessageFragment()
      ]
    });
    const orchestrator = events.find((e) => e.kind === 'decision.orchestrator');
    const user = events.find((e) => e.kind === 'message.user');
    // The core agent model must not leak onto rows the log cannot attribute.
    expect(orchestrator?.model ?? null).toBeNull();
    expect(user?.model ?? null).toBeNull();
  });

  it('attaches the current run model to a contiguous tool burst', () => {
    const events = projectConversation({
      source: SOURCE,
      lines: [...taskboardStartedFragment('claude-opus-4-8'), ...toolBurstFragment()]
    });
    const burst = events.find((e) => e.kind === 'toolBurst');
    expect(burst?.model).toBe('claude-opus-4-8');
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
    expect(probe(image).durablePath).toBe('results/01-empty-state.png');
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
    expect(probe(metric).inputTokens).toBe(1500);
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

  it('emits a watchdog wait loop as quiet → quiet → quiet → resumed', () => {
    const events = projectConversation({ source: SOURCE, lines: waitLoopFragment() });
    expect(events.map((e) => e.kind)).toEqual([
      'supervisor.wait',
      'supervisor.wait',
      'supervisor.wait',
      'supervisor.wait'
    ]);
    expect(probe(events[0]).state).toBe('quiet');
    expect(probe(events[3]).state).toBe('resumed');
    // Trace preservation: every wait row must point back into the source.
    for (const ev of events) {
      expect(ev.rawRange.source).toBe(SOURCE);
      expect(ev.rawRange.start).toBeGreaterThan(0);
      expect(ev.rawRange.end).toBeGreaterThanOrEqual(ev.rawRange.start);
    }
  });

  it('emits a system.schemaDrift event for unparseable structured reports', () => {
    const events = projectConversation({ source: SOURCE, lines: schemaDriftFragment() });
    expect(events).toHaveLength(1);
    expect(events[0].kind).toBe('system.schemaDrift');
    expect(probe(events[0]).expectedSchema).toBe('MetaCycleReport');
    expect(probe(events[0]).rawLink?.range.source).toBe(SOURCE);
    expect(events[0].severity).toBe('warn');
  });

  it('captures a token spike via metric.token and surfaces it in the summary aggregate', () => {
    const events = projectConversation({
      source: SOURCE,
      lines: tokenSpikeFragment(),
      tokenSummary: tokenSpikeSummary(),
      emitWorkbenchSummary: true
    });
    const metric = events.find((e) => e.kind === 'metric.token');
    expect(metric).toBeDefined();
    expect(probe(metric).inputTokens).toBe(280_000);

    const summary = events.find((e) => e.kind === 'workbench.summary');
    expect(summary).toBeDefined();
    const aggregate = probe(summary).aggregate;
    expect(aggregate.totalInputTokens).toBe(280_000);
    expect(aggregate.totalOutputTokens).toBe(14_500);
    // Headline must reference tokens so the summary strip can show pressure.
    expect(probe(summary).headline).toMatch(/token/i);
  });

  it('models a test fail/retry/pass burst as one merged burst with failure + tests rollup', () => {
    const events = projectConversation({
      source: SOURCE,
      lines: testFailRetryFragment(),
      emitWorkbenchSummary: true
    });
    const tools = events.filter((e) => e.kind === 'toolBurst').map(probe);
    // Fail/retry/pass is one tool burst, not three. Failure stays visible in
    // both the burst row and the summary headline.
    expect(tools).toHaveLength(1);
    const burst = tools[0];
    expect(burst.failures).toBeGreaterThan(0);
    expect(burst.severity).toBe('error');
    expect(burst.tests).toBeDefined();
    expect(burst.tests.length).toBe(1);
    // Final status survives the retry: the latest non-unknown status wins.
    expect(burst.tests[0].status).toBe('pass');

    const summary = probe(events.find((e) => e.kind === 'workbench.summary'));
    expect(summary.aggregate?.toolFailureCount).toBeGreaterThan(0);
    expect(summary.headline).toMatch(/failure/);
  });

  it('extracts touched files and artifact paths from contiguous tool bursts', () => {
    const events = projectConversation({
      source: SOURCE,
      lines: [
        ...toolBurstFragment(),
        ...imageArtifactFragment()
      ]
    });
    const tools = events.filter((e) => e.kind === 'toolBurst').map(probe);
    expect(tools).toHaveLength(1);
    const burst = tools[0];
    expect(burst.files).toBeDefined();
    // Files come from read / search / edit groups (subtitle + verb-derived).
    expect(burst.files.some((f: string) => f.includes('prompt.md'))).toBe(true);
    expect(burst.files.some((f: string) => f.includes('feature-flags.service.ts'))).toBe(true);
    // Artifacts split out from the file list when the path looks like a
    // result / screenshot / report.
    expect(burst.artifacts).toBeDefined();
    expect(burst.artifacts.some((a: string) => a.endsWith('.png'))).toBe(true);
  });

  it('does not merge tool bursts across an agent reply', () => {
    const events = projectConversation({
      source: SOURCE,
      lines: [
        ...toolBurstFragment(),
        ...agentTextFragment(),
        ...toolBurstFragment()
      ]
    });
    const tools = events.filter((e) => e.kind === 'toolBurst');
    // The agent prose breaks the burst so the chat reads as
    // tool-burst → reply → tool-burst.
    expect(tools).toHaveLength(2);
    const agent = events.find((e) => e.kind === 'message.taskAgent');
    expect(agent).toBeDefined();
  });

  it('produces a workbench.summary aggregate with state, run, tokens, commits, files, screenshots, and warnings', () => {
    const events = projectConversation({
      source: SOURCE,
      lines: compositeFragment(),
      runTimeline: runTimelineForComposite(),
      job: {
        id: 'fixture-job',
        taskKey: 'wp::fixture-job',
        title: 'Fixture',
        state: '3-progress',
        order: 0,
        agent: 'claude',
        createdAt: '2026-05-05T11:55:00Z',
        watchPath: 'C:/wp',
        projectName: 'agent-taskboard',
        folderPath: '',
        lastActivity: '2026-05-05T12:02:00Z',
        sessionName: null,
        model: 'claude-opus-4-7',
        cliType: 'claude',
        useOwnSession: false,
        lastUsage: null,
        execution: null,
        commit: null
      },
      tokenSummary: {
        calls: 1,
        inputTokens: 1200,
        outputTokens: 250,
        cacheReadTokens: 0,
        cacheCreationTokens: 0,
        totalTokens: 1450,
        lastModel: 'claude-opus-4-7',
        lastUpdate: '2026-05-05T12:02:00Z',
        entries: []
      },
      commits: [
        {
          sha: 'aaa',
          shortSha: 'aaa',
          subject: 'feat: x',
          authorDateUtc: '2026-05-05T12:01:00Z',
          files: [
            { status: 'M', path: 'a.ts', added: 1, removed: 0 },
            { status: 'M', path: 'b.ts', added: 2, removed: 1 }
          ]
        }
      ],
      screenshots: [
        { caption: 'one', sourcePath: 'results/a.png', durablePath: 'results/a.png' }
      ],
      latestResult: '[[TASK_DONE]]',
      emitWorkbenchSummary: true,
      emitWorkbenchPreviews: true,
      emitRunMarkers: true
    });

    const summary = probe(events.find((e) => e.kind === 'workbench.summary'));
    expect(summary).toBeDefined();
    const a = summary.aggregate;
    expect(a.state).toBe('3-progress');
    expect(a.runCount).toBe(1);
    expect(a.latestRunStatus).toBe('completed');
    expect(a.totalDurationSeconds).toBe(120);
    expect(a.totalInputTokens).toBe(1200);
    expect(a.totalOutputTokens).toBe(250);
    expect(a.commitCount).toBe(1);
    expect(a.filesChanged).toBe(2);
    expect(a.screenshotCount).toBe(1);
    expect(a.toolCallCount).toBeGreaterThan(0);
    expect(a.retryWarningCount).toBeUndefined();
    expect(a.latestResult).toBe('[[TASK_DONE]]');
    expect(summary.headline).toMatch(/commit/);
  });

  it('emits a workbench.debug aggregate with actor, tool, warning, token, and run rollups', () => {
    const events = projectConversation({
      source: SOURCE,
      lines: [
        ...compositeFragment(),
        ...captureFailFragment(),
        ...heuristicWarningFragment(),
        ...schemaDriftFragment()
      ],
      runTimeline: runTimelineForComposite(),
      tokenSummary: {
        calls: 1,
        inputTokens: 1200,
        outputTokens: 250,
        cacheReadTokens: 0,
        cacheCreationTokens: 0,
        totalTokens: 1450,
        lastModel: 'claude-opus-4-7',
        lastUpdate: '2026-05-05T12:02:00Z',
        entries: []
      },
      emitDebugAggregate: true
    });
    const debug = probe(events.find((e) => e.kind === 'workbench.debug'));
    expect(debug.actorCounts.user).toBeGreaterThan(0);
    expect(debug.actorCounts.taskAgent).toBeGreaterThan(0);
    expect(debug.toolDensity.total).toBeGreaterThan(0);
    expect(debug.warningCounts.captureFails).toBe(1);
    expect(debug.warningCounts.parserWarnings).toBe(1);
    expect(debug.warningCounts.schemaDrifts).toBe(1);
    expect(debug.warningCounts.watchdogQuiet).toBeGreaterThanOrEqual(1);
    expect(debug.tokenTotals.inputTokens).toBe(1200);
    expect(debug.runStats.runCount).toBe(1);
    expect(debug.runStats.completedCount).toBe(1);
    expect(debug.traceLinks.length).toBeGreaterThan(0);
    for (const link of debug.traceLinks) {
      expect(link.range.source).toBe(SOURCE);
      expect(link.range.end).toBeGreaterThanOrEqual(link.range.start);
    }
  });

  it('preserves trace addressability: every emitted event keeps a 1-based raw range into the source log', () => {
    const lines = compositeFragment();
    const events = projectConversation({
      source: SOURCE,
      lines,
      runTimeline: runTimelineForComposite(),
      emitWorkbenchSummary: true,
      emitDebugAggregate: true,
      emitTraceLink: true
    });
    expect(events.length).toBeGreaterThan(0);
    for (const ev of events) {
      expect(ev.rawRange.source).toBe(SOURCE);
      expect(ev.rawRange.start).toBeGreaterThanOrEqual(1);
      expect(ev.rawRange.end).toBeGreaterThanOrEqual(ev.rawRange.start);
      expect(ev.rawRange.end).toBeLessThanOrEqual(lines.length);
    }
    // The dedicated trace link is the explicit "open raw" handle the renderer
    // uses; it must address the full transcript.
    const trace = probe(events.find((e) => e.kind === 'traceLink'));
    expect(trace.link.range.start).toBe(1);
    expect(trace.link.range.end).toBe(lines.length);
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
    expect(CONVERSATION_EVENT_KINDS).toContain('workbench.debug');
    expect(CONVERSATION_EVENT_KINDS).toContain('system.status');
    expect(CONVERSATION_EVENT_KINDS).toContain('system.schemaDrift');
    expect(CONVERSATION_EVENT_KINDS).toContain('feedback.queued');
  });
});
