import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { RunTimelinePollService } from '../../../../polling/services/run-timeline-poll.service';
import { AgentWorkSummaryPollService } from '../../../../polling/services/agent-work-summary-poll.service';
import { TaskPipelinePollService } from '../../../../polling/services/task-pipeline-poll.service';
import { TaskTimelinePollService } from '../../../../polling/services/task-timeline-poll.service';
import { OverviewPaneComponent } from './overview-pane.component';
import type { TaskInfo } from '../../../../../models/task.model';
import type { AgentWorkSummary } from '../../../../session-events';
import type { TaskPipelineResponse } from '../../../../task-pipeline';
import type { RunRecord, RunTimeline } from '../../../../run-timeline';

/** A pipeline catalogue + execution with a single core Agent-execution row. */
function agentPipeline(coreModel: string | null = 'claude-opus-4-8'): TaskPipelineResponse {
  return {
    pipeline: {
      id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
      pre: [], core: [], post: [],
      allSteps: [
        { id: 'core-agent-run', displayName: 'Agent execution', kind: 'core', runMode: 'sequential', dependsOn: [], idempotent: false, stub: false },
        { id: 'aspect-code-quality', displayName: 'Code quality', kind: 'aspect', runMode: 'parallel', dependsOn: [], idempotent: true, stub: false },
      ],
    },
    execution: {
      pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
      startedAt: new Date().toISOString(), completedAt: null,
      steps: [
        { stepId: 'core-agent-run', kind: 'core', model: coreModel ?? undefined, status: 'running', startedAt: new Date().toISOString(), completedAt: null, durationMs: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 },
      ],
    },
    cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
    config: {},
  };
}

function runRecord(overrides: Partial<RunRecord> = {}): RunRecord {
  return {
    index: 0, intent: 'start', startedAt: new Date().toISOString(), endedAt: null,
    status: 'completed', cli: 'claude', exitCode: 0, durationSeconds: 12,
    inputSessionId: null, capturedSessionId: null, resumed: false, reason: null,
    userFollowup: null, lineStart: null, lineEnd: null,
    headShaBefore: null, headShaAfter: null, contextRef: null,
    ...overrides,
  };
}

function runTimeline(runCount: number, runs: RunRecord[] = [], extra: Partial<RunTimeline> = {}): RunTimeline {
  return {
    runCount,
    firstStartedAt: runs[0]?.startedAt ?? null,
    lastActivityAt: runs[runs.length - 1]?.startedAt ?? null,
    hasActiveRun: false,
    runs,
    ...extra,
  };
}

function baseJob(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'test-1', taskKey: 'wp::test-1', key: 'wp::test-1', title: 'Test', state: '2-ready',
    order: 1, agent: 'human', createdAt: new Date().toISOString(),
    watchPath: '/tmp', projectName: 'test', folderPath: '/tmp/test-1',
    lastActivity: new Date().toISOString(), sessionName: null,
    model: null, cliType: null, useOwnSession: null, lastUsage: null,
    execution: null, commit: null,
    ...overrides,
  };
}

async function build(job: TaskInfo, agentWork: AgentWorkSummary | null = null) {
  await TestBed.configureTestingModule({
    imports: [OverviewPaneComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      RunTimelinePollService,
      AgentWorkSummaryPollService,
      TaskPipelinePollService,
      TaskTimelinePollService,
    ],
  }).compileComponents();
  if (agentWork) {
    TestBed.inject(AgentWorkSummaryPollService).summary.set(agentWork);
  }
  const fixture = TestBed.createComponent(OverviewPaneComponent);
  fixture.componentRef.setInput('job', job);
  try { fixture.detectChanges(); } catch (e) {
    console.warn('[smoke] OverviewPaneComponent initial render skipped:', (e as Error).message);
  }
  return fixture;
}

describe('OverviewPaneComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    const fixture = await build(baseJob());
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('agentUsage falls back to lastUsage when present (so TOKENS is not empty after an agent run)', async () => {
    const fixture = await build(baseJob({
      state: '4-auto-review',
      lastUsage: { at: new Date().toISOString(), tokens: '12.4k', changes: '5 files', requests: '8' },
    }));
    const c = fixture.componentInstance;
    expect(c.hasOrchestratorTokens()).toBe(false);
    expect(c.agentUsage()).not.toBeNull();
    expect(c.agentUsage()!.tokens).toBe('12.4k');
  });

  it('agentUsage stays null when lastUsage is present but all fields are empty', async () => {
    const fixture = await build(baseJob({
      lastUsage: { at: new Date().toISOString(), tokens: null, changes: null, requests: null },
    }));
    expect(fixture.componentInstance.agentUsage()).toBeNull();
  });

  it('empty-state copy depends on lane: ready vs running vs completed', async () => {
    const fixture = await build(baseJob({ state: '2-ready' }));
    const c = fixture.componentInstance;
    expect(c.tokensEmptyMessage()).toMatch(/Run not started/i);

    fixture.componentRef.setInput('job', baseJob({ state: '3-progress' }));
    try { fixture.detectChanges(); } catch { /* ignore */ }
    expect(c.tokensEmptyMessage()).toMatch(/in progress/i);

    fixture.componentRef.setInput('job', baseJob({ state: '6-completed' }));
    try { fixture.detectChanges(); } catch { /* ignore */ }
    expect(c.tokensEmptyMessage()).toMatch(/No token activity recorded/i);
  });

  it('agent-work block surfaces call count + tool counts from the poll service', async () => {
    const fixture = await build(
      baseJob({ state: '6-completed', sessionName: 'sess-1' }),
      {
        calls: 3,
        recovered: false,
        toolCalls: 42,
        toolCounts: [
          { tool: 'Read', count: 24 },
          { tool: 'Edit', count: 12 },
          { tool: 'Bash', count: 6 },
        ],
        startedAt: new Date(Date.now() - 60_000).toISOString(),
        lastTouchAt: new Date().toISOString(),
        currentSessionId: 'sess-1',
      },
    );
    const c = fixture.componentInstance;
    expect(c.hasAgentWork()).toBe(true);
    expect(c.agentWork()!.calls).toBe(3);
    expect(c.topToolCounts().map(tc => tc.tool)).toEqual(['Read', 'Edit', 'Bash']);
    expect(c.toolCountsTooltip()).toContain('Read: 24');
    expect(c.sessionDebugTooltip()).toContain('sess-1');
  });

  it('agent-work block hides when there is no work yet', async () => {
    const fixture = await build(baseJob({ state: '2-ready' }), {
      calls: 0,
      recovered: false,
      toolCalls: 0,
      toolCounts: [],
      startedAt: null,
      lastTouchAt: null,
      currentSessionId: null,
    });
    expect(fixture.componentInstance.hasAgentWork()).toBe(false);
  });

  it('hero title block: displayedTitle falls back to job.id when title is missing', async () => {
    const fixture = await build(baseJob({ title: '', id: 'fallback-task-id' }));
    expect(fixture.componentInstance.displayedTitle()).toBe('fallback-task-id');
  });

  it('hero title block: startTitleEdit seeds the draft and flips editingTitle', async () => {
    const fixture = await build(baseJob({ title: 'Original title' }));
    const c = fixture.componentInstance;
    expect(c.editingTitle()).toBe(false);
    c.startTitleEdit();
    expect(c.editingTitle()).toBe(true);
    expect(c.titleDraft()).toBe('Original title');
    c.cancelTitleEdit();
    expect(c.editingTitle()).toBe(false);
  });

  it('hero title block: saving an unchanged title just exits edit mode (no PUT, no override)', async () => {
    const fixture = await build(baseJob({ title: 'Same title' }));
    const c = fixture.componentInstance;
    c.startTitleEdit();
    c.onTitleDraftInput('   Same title   ');
    c.saveTitle();
    expect(c.editingTitle()).toBe(false);
    // displayedTitle still reflects the underlying job because no optimistic
    // override was set.
    expect(c.displayedTitle()).toBe('Same title');
  });

  it('pipeline block: joins catalogue + execution + cost into per-step rows and a task total', async () => {
    const fixture = await build(baseJob({ state: '4-auto-review' }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'core-agent-run', displayName: 'Agent execution', kind: 'core', runMode: 'sequential', dependsOn: [], idempotent: false, stub: false },
          { id: 'aspect-code-quality', displayName: 'Code quality', kind: 'aspect', runMode: 'parallel', dependsOn: [], idempotent: true, stub: false },
          { id: 'aspect-tests-and-evidence', displayName: 'Tests and evidence', kind: 'aspect', runMode: 'parallel', dependsOn: [], idempotent: true, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: new Date().toISOString(), completedAt: new Date().toISOString(),
        steps: [
          { stepId: 'aspect-code-quality', kind: 'aspect', model: 'claude-haiku-4-5', status: 'passed', durationMs: 1200, inputTokens: 1000, outputTokens: 200, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'pass' },
        ],
      },
      cost: {
        steps: [
          { stepId: 'aspect-code-quality', kind: 'aspect', model: 'claude-haiku-4-5', modelKnown: true, inputTokens: 1000, outputTokens: 200, cacheReadTokens: 0, cacheCreationTokens: 0, totalTokens: 1200, costUsd: 0.002 },
        ],
        totalTokens: 1200, totalCostUsd: 0.002, anyModelUnknown: false,
      },
      // tests-and-evidence disabled at project level; code-quality forced to Haiku.
      config: {
        'aspect-tests-and-evidence': { enabled: false, model: null, mode: null },
        'aspect-code-quality': { enabled: true, model: 'claude-haiku-4-5', mode: null },
      },
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    const rows = c.pipelineRows();
    expect(rows.map(r => r.id)).toEqual(['core-agent-run', 'aspect-code-quality', 'aspect-tests-and-evidence']);

    const core = rows.find(r => r.id === 'core-agent-run')!;
    expect(core.status).toBe('pending'); // no execution row, not a stub

    const cq = rows.find(r => r.id === 'aspect-code-quality')!;
    expect(cq.status).toBe('passed');
    expect(cq.model).toBe('claude-haiku-4-5');
    expect(cq.verdict).toBe('pass');
    expect(cq.totalTokens).toBe(1200);
    expect(cq.costKnown).toBe(true);

    const tests = rows.find(r => r.id === 'aspect-tests-and-evidence')!;
    expect(tests.status).toBe('disabled');
    expect(tests.enabled).toBe(false);

    expect(c.hasPipeline()).toBe(true);
    expect(c.pipelineTotal()).toEqual({ totalTokens: 1200, totalCostUsd: 0.002, anyModelUnknown: false });
    expect(c.formatCost(0.002)).toBe('$0.0020');
    expect(c.formatCost(1.5)).toBe('$1.50');
  });

  it('pipeline block: an in-flight execution row surfaces a running status the template can highlight', async () => {
    const fixture = await build(baseJob({ state: '3-progress' }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'core-agent-run', displayName: 'Agent execution', kind: 'core', runMode: 'sequential', dependsOn: [], idempotent: false, stub: false },
          { id: 'aspect-code-quality', displayName: 'Code quality', kind: 'aspect', runMode: 'parallel', dependsOn: [], idempotent: true, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: new Date().toISOString(), completedAt: null,
        steps: [
          { stepId: 'core-agent-run', kind: 'core', model: 'claude-opus-4-7', status: 'running', startedAt: new Date().toISOString(), completedAt: null, durationMs: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 },
        ],
      },
      cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    const rows = c.pipelineRows();
    const core = rows.find(r => r.id === 'core-agent-run')!;
    expect(core.status).toBe('running');
    // A step the runtime has not reached yet stays pending, so only the one
    // active step lights up.
    const cq = rows.find(r => r.id === 'aspect-code-quality')!;
    expect(cq.status).toBe('pending');
    expect(c.stepStatusLabel('running')).toBe('Running');
  });

  it('pipeline block: per-step rows carry start/end stamps and a live-counting duration for the running step', async () => {
    const fixture = await build(baseJob({ state: '3-progress' }));
    const runningStart = new Date(Date.now() - 4_000).toISOString();
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'core-agent-run', displayName: 'Agent execution', kind: 'core', runMode: 'sequential', dependsOn: [], idempotent: false, stub: false },
          { id: 'aspect-code-quality', displayName: 'Code quality', kind: 'aspect', runMode: 'parallel', dependsOn: [], idempotent: true, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: '2026-06-02T08:00:00Z', completedAt: null,
        steps: [
          { stepId: 'aspect-code-quality', kind: 'aspect', model: 'm', status: 'passed', startedAt: '2026-06-02T08:00:00Z', completedAt: '2026-06-02T08:00:42Z', durationMs: 42_000, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'pass' },
          { stepId: 'core-agent-run', kind: 'core', model: 'm', status: 'running', startedAt: runningStart, completedAt: null, durationMs: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 },
        ],
      },
      cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    const rows = c.pipelineRows();
    const done = rows.find(r => r.id === 'aspect-code-quality')!;
    const running = rows.find(r => r.id === 'core-agent-run')!;
    const pending = rows.find(r => r.id === 'aspect-code-quality');

    // Rows carry the raw stamps so the table can render times.
    expect(done.startedAt).toBe('2026-06-02T08:00:00Z');
    expect(done.completedAt).toBe('2026-06-02T08:00:42Z');
    expect(running.startedAt).toBe(runningStart);
    expect(running.completedAt).toBeNull();

    // A completed step shows its recorded duration, independent of the clock.
    expect(c.liveStepDurationMs(done)).toBe(42_000);
    // The running step counts up from its start (≈ now − startedAt), so the
    // value is the live elapsed time, not the recorded 0.
    const live = c.liveStepDurationMs(running);
    expect(live).toBeGreaterThanOrEqual(3_000);
    expect(live).toBeLessThan(60_000);

    // Wall-clock formatter is locale-tolerant: HH:MM, empty for unset.
    expect(c.formatClock(done.startedAt)).toMatch(/\d{1,2}:\d{2}/);
    expect(c.formatClock(null)).toBe('');
    expect(c.formatClock('not-a-date')).toBe('');

    // Timing tooltip: start + end + duration for a finished step; a live
    // "running for" line while in flight; null before the step starts.
    const doneTip = c.stepTimingTooltip(done)!;
    expect(doneTip.title).toBe('Code quality');
    expect(doneTip.body).toContain('Started:');
    expect(doneTip.body).toContain('Ended:');
    expect(doneTip.body).toContain('Duration:');

    const runTip = c.stepTimingTooltip(running)!;
    expect(runTip.body).toContain('Running for');
    expect(runTip.body).not.toContain('Ended:');

    expect(pending).toBeTruthy();
    // A row with no start stamp carries no timing tooltip.
    const noStart = { ...done, startedAt: null, completedAt: null, status: 'pending' as const };
    expect(c.stepTimingTooltip(noStart)).toBeNull();
  });

  it('pipeline block: concern tooltip is built for a non-pass aspect verdict, and absent for pass', async () => {
    const fixture = await build(baseJob({ state: '4-auto-review' }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'aspect-requirement-fit', displayName: 'Requirement fit', kind: 'aspect', runMode: 'parallel', dependsOn: [], idempotent: true, stub: false },
          { id: 'aspect-code-quality', displayName: 'Code quality', kind: 'aspect', runMode: 'parallel', dependsOn: [], idempotent: true, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: new Date().toISOString(), completedAt: new Date().toISOString(),
        steps: [
          { stepId: 'aspect-requirement-fit', kind: 'aspect', model: 'm', status: 'passed', durationMs: 1, inputTokens: 1, outputTokens: 1, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'concerns', verdictSummary: 'Acceptance item 3 (empty-state tooltip) has no evidence.' },
          { stepId: 'aspect-code-quality', kind: 'aspect', model: 'm', status: 'passed', durationMs: 1, inputTokens: 1, outputTokens: 1, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'pass' },
        ],
      },
      cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const rows = fixture.componentInstance.pipelineRows();
    const concerned = rows.find(r => r.id === 'aspect-requirement-fit')!;
    expect(concerned.verdict).toBe('concerns');
    expect(concerned.concernTooltip).not.toBeNull();
    expect(concerned.concernTooltip!.title).toBe('Requirement fit · Concerns');
    expect(concerned.concernTooltip!.body).toContain('Acceptance item 3');

    // A pass verdict (or a verdict with no summary) must not grow a tooltip.
    const passing = rows.find(r => r.id === 'aspect-code-quality')!;
    expect(passing.verdict).toBe('pass');
    expect(passing.concernTooltip).toBeNull();
  });

  it('pipeline block: a circuit-broken loop guard surfaces a loop-detected verdict as the first row with a concern tooltip', async () => {
    const fixture = await build(baseJob({ state: '3-progress' }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'pre-loop-guard', displayName: 'Loop check', kind: 'module', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false },
          { id: 'core-agent-run', displayName: 'Agent execution', kind: 'core', runMode: 'sequential', dependsOn: [], idempotent: false, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: new Date().toISOString(), completedAt: null,
        steps: [
          { stepId: 'pre-loop-guard', kind: 'module', status: 'failed', durationMs: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'loop-detected', verdictSummary: 'Auto-loop circuit-breaker fired after 5/5 iterations; awaiting user.' },
          { stepId: 'core-agent-run', kind: 'core', status: 'passed', durationMs: 1, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 },
        ],
      },
      cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    const rows = c.pipelineRows();
    // The loop guard is the first row (Pre), so a detected loop is visible early.
    expect(rows[0].id).toBe('pre-loop-guard');
    expect(c.stepKindLabel(rows[0].kind)).toBe('Pre');

    const guard = rows.find(r => r.id === 'pre-loop-guard')!;
    expect(guard.status).toBe('failed');
    expect(guard.verdict).toBe('loop-detected');
    expect(guard.concernTooltip).not.toBeNull();
    expect(guard.concernTooltip!.title).toBe('Loop check · Loop detected');
    expect(guard.concernTooltip!.body).toContain('circuit-breaker');
  });

  it('pipeline block: a failed CORE step that still claims a success verdict drops the contradictory badge (ASS-2)', async () => {
    // Bug ASS-2: a legacy on-disk record persisted the CORE step with
    // status='failed' (red ❌ icon) AND verdict='success' (green SUCCESS badge)
    // at once. The status is authoritative, so the success badge is suppressed
    // and the row tells one story.
    const fixture = await build(baseJob({ state: '3-progress' }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'core-agent-run', displayName: 'Agent execution', kind: 'core', runMode: 'sequential', dependsOn: [], idempotent: false, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: new Date().toISOString(), completedAt: new Date().toISOString(),
        steps: [
          { stepId: 'core-agent-run', kind: 'core', status: 'failed', durationMs: 1, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'success' },
        ],
      },
      cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    const core = c.pipelineRows().find(r => r.id === 'core-agent-run')!;
    // Icon stays Failed; the contradictory success verdict is reconciled away.
    expect(core.status).toBe('failed');
    expect(c.stepStatusIcon(core.status)).toBe('❌');
    expect(core.verdict).toBeNull();

    // And the DOM carries no SUCCESS badge on the core row.
    const coreRow = fixture.nativeElement.querySelector('[data-step-id="core-agent-run"]');
    expect(coreRow).not.toBeNull();
    expect(coreRow.querySelector('[data-testid="overview-pipeline-step-verdict"]')).toBeNull();
  });

  it('pipeline block: a passed CORE step keeps its success verdict (consistent record is untouched)', async () => {
    const fixture = await build(baseJob({ state: '3-progress' }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'core-agent-run', displayName: 'Agent execution', kind: 'core', runMode: 'sequential', dependsOn: [], idempotent: false, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: new Date().toISOString(), completedAt: new Date().toISOString(),
        steps: [
          { stepId: 'core-agent-run', kind: 'core', status: 'passed', durationMs: 1, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'success' },
        ],
      },
      cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const core = fixture.componentInstance.pipelineRows().find(r => r.id === 'core-agent-run')!;
    expect(core.status).toBe('passed');
    expect(core.verdict).toBe('success');
  });

  it('pipeline block: a forming loop under budget reads as a passed guard with a looping verdict', async () => {
    const fixture = await build(baseJob({ state: '3-progress' }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'pre-loop-guard', displayName: 'Loop check', kind: 'module', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: new Date().toISOString(), completedAt: null,
        steps: [
          { stepId: 'pre-loop-guard', kind: 'module', status: 'passed', durationMs: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'looping', verdictSummary: 'Auto-mode loop forming: 2/5 iterations, 40000/200000 orchestrator tokens used.' },
        ],
      },
      cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const guard = fixture.componentInstance.pipelineRows().find(r => r.id === 'pre-loop-guard')!;
    expect(guard.status).toBe('passed');
    expect(guard.verdict).toBe('looping');
    expect(guard.concernTooltip).not.toBeNull();
    expect(guard.concernTooltip!.title).toBe('Loop check · Loop forming');
  });

  it('pipeline block: parallel aspects carry a parallel badge and the orchestrator decision renders as a separate final-verdict step', async () => {
    const fixture = await build(baseJob({ state: '4-auto-review' }));
    const pipe: TaskPipelineResponse = {
      pipeline: {
        id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
        pre: [], core: [], post: [],
        allSteps: [
          { id: 'aspect-requirement-fit', displayName: 'Requirement fit', kind: 'aspect', runMode: 'parallel', dependsOn: [], idempotent: true, stub: false },
          { id: 'aspect-code-quality', displayName: 'Code quality', kind: 'aspect', runMode: 'parallel', dependsOn: [], idempotent: true, stub: false },
          { id: 'post-orchestrator-decision', displayName: 'Final verdict', kind: 'orchestrator', runMode: 'sequential', dependsOn: [], idempotent: true, stub: false },
        ],
      },
      execution: {
        pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: 'test-1', project: 'test',
        startedAt: new Date().toISOString(), completedAt: new Date().toISOString(),
        steps: [
          { stepId: 'aspect-requirement-fit', kind: 'aspect', model: 'm', status: 'passed', durationMs: 1, inputTokens: 1, outputTokens: 1, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'pass' },
          { stepId: 'aspect-code-quality', kind: 'aspect', model: 'm', status: 'passed', durationMs: 1, inputTokens: 1, outputTokens: 1, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'pass' },
          { stepId: 'post-orchestrator-decision', kind: 'orchestrator', model: 'm', status: 'passed', durationMs: 1, inputTokens: 1, outputTokens: 1, cacheReadTokens: 0, cacheCreationTokens: 0, verdict: 'accept', verdictSummary: 'All aspects passed; accepting.' },
        ],
      },
      cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
      config: {},
    };
    TestBed.inject(TaskPipelinePollService).pipeline.set(pipe);
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    const rows = c.pipelineRows();

    // Aspect rows are flagged parallel so the template can badge them.
    const aspects = rows.filter(r => r.kind === 'aspect');
    expect(aspects.length).toBe(2);
    expect(aspects.every(r => r.runMode === 'parallel')).toBe(true);

    // The orchestrator decision is its own, separate row (Req 3).
    const decision = rows.find(r => r.id === 'post-orchestrator-decision')!;
    expect(decision.kind).toBe('orchestrator');
    expect(decision.runMode).toBe('sequential');
    expect(decision.verdict).toBe('accept');
    expect(c.stepKindLabel(decision.kind)).toBe('Decision');

    // The DOM carries the parallel badge on aspect rows and exactly one
    // final-verdict chip on the orchestrator row.
    const parallelBadges = fixture.nativeElement.querySelectorAll('[data-testid="overview-pipeline-step-parallel"]');
    expect(parallelBadges.length).toBe(2);
    const finalChips = fixture.nativeElement.querySelectorAll('[data-testid="overview-pipeline-step-final-verdict"]');
    expect(finalChips.length).toBe(1);

    // The decision row is marked so the template can visually separate it.
    const decisionEl = fixture.nativeElement.querySelector('[data-step-id="post-orchestrator-decision"]') as HTMLElement | null;
    expect(decisionEl).not.toBeNull();
    expect(decisionEl!.classList.contains('ov-pl-step--final-verdict')).toBe(true);
    expect(decisionEl!.getAttribute('data-run-mode')).toBe('sequential');
  });

  it('promote affordance: shown only on a finished planning task across its finished lanes', async () => {
    const fixture = await build(baseJob({ mode: 'planning', state: '4-auto-review' }));
    const c = fixture.componentInstance;
    expect(c.canPromote()).toBe(true);
    for (const state of ['5-human-review', '6-completed']) {
      fixture.componentRef.setInput('job', baseJob({ mode: 'planning', state }));
      try { fixture.detectChanges(); } catch { /* ignore */ }
      expect(c.canPromote()).toBe(true);
    }
  });

  it('promote affordance: hidden on a planning task that has not finished', async () => {
    const fixture = await build(baseJob({ mode: 'planning', state: '1-preparation' }));
    const c = fixture.componentInstance;
    expect(c.canPromote()).toBe(false);
    for (const state of ['2-ready', '3-progress', '1b-needs-human-review']) {
      fixture.componentRef.setInput('job', baseJob({ mode: 'planning', state }));
      try { fixture.detectChanges(); } catch { /* ignore */ }
      expect(c.canPromote()).toBe(false);
    }
  });

  it('promote affordance: hidden on research tasks even when finished (research is read-only)', async () => {
    const fixture = await build(baseJob({ mode: 'research', state: '6-completed' }));
    expect(fixture.componentInstance.canPromote()).toBe(false);
  });

  it('promote affordance: hidden on coding tasks and on legacy payloads with no mode', async () => {
    const fixture = await build(baseJob({ mode: 'coding', state: '6-completed' }));
    const c = fixture.componentInstance;
    expect(c.canPromote()).toBe(false);

    // Legacy payloads omit `mode`; the affordance must stay hidden (read as coding).
    fixture.componentRef.setInput('job', baseJob({ state: '6-completed' }));
    try { fixture.detectChanges(); } catch { /* ignore */ }
    expect(c.canPromote()).toBe(false);
  });

  it('promote affordance: the promote button is in the DOM for a finished planning task, absent for research', async () => {
    const fixture = await build(baseJob({ mode: 'planning', state: '5-human-review' }));
    expect(
      fixture.nativeElement.querySelector('[data-testid="overview-promote-btn"]'),
    ).not.toBeNull();

    fixture.componentRef.setInput('job', baseJob({ mode: 'research', state: '5-human-review' }));
    try { fixture.detectChanges(); } catch { /* ignore */ }
    expect(
      fixture.nativeElement.querySelector('[data-testid="overview-promote-btn"]'),
    ).toBeNull();
  });

  it('agent-execution row: run count is read from the run-timeline (same source as the Overview Runs value)', async () => {
    const runs = [
      runRecord({ index: 0, intent: 'start' }),
      runRecord({ index: 1, intent: 'continue' }),
      runRecord({ index: 2, intent: 'recovery' }),
    ];
    const fixture = await build(
      baseJob({ state: '3-progress', sessionName: 'sess-xyz', cliType: 'claude', model: 'claude-opus-4-8' }),
    );
    TestBed.inject(TaskPipelinePollService).pipeline.set(agentPipeline());
    TestBed.inject(RunTimelinePollService).timeline.set(runTimeline(34, runs));
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    expect(c.agentRunCount()).toBe(34);
    expect(c.agentRunCountLabel()).toBe('34 runs');

    const tip = c.agentRunTooltip()!;
    expect(tip).not.toBeNull();
    expect(tip.title).toBe('Agent execution · 34 runs');
    expect(tip.body).toContain('Runs: 34');
    expect(tip.body).toContain('Recovered: 1');
    expect(tip.body).toContain('Model: claude-opus-4-8');
    expect(tip.body).toContain('Session: sess-xyz');
    expect(tip.body).toContain('See the Timeline tab');

    // The badge renders only on the core row, as a focusable <button>.
    const badge = fixture.nativeElement.querySelector('[data-testid="overview-pipeline-agent-runs"]') as HTMLElement | null;
    expect(badge).not.toBeNull();
    expect(badge!.tagName).toBe('BUTTON');
    expect(badge!.textContent?.trim()).toBe('34 runs');
    expect(badge!.getAttribute('data-run-count')).toBe('34');
    expect(badge!.getAttribute('aria-label')).toContain('34 runs');
  });

  it('agent-execution row: falls back to the Agent Work call count when the run-timeline is empty', async () => {
    const fixture = await build(
      baseJob({ state: '3-progress' }),
      {
        calls: 5, recovered: true, toolCalls: 0, toolCounts: [],
        startedAt: new Date().toISOString(), lastTouchAt: new Date().toISOString(),
        currentSessionId: 'sess-aw',
      },
    );
    TestBed.inject(TaskPipelinePollService).pipeline.set(agentPipeline());
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    expect(c.agentRunCount()).toBe(5);
    expect(c.agentRunCountLabel()).toBe('5 runs');
    // Recovered flag (no run-timeline intents available) still surfaces as 1.
    expect(c.agentRunTooltip()!.body).toContain('Recovered: 1');
    expect(c.agentRunTooltip()!.body).toContain('Session: sess-aw');
  });

  it('agent-execution row: a single run reads as "1 run"', async () => {
    const fixture = await build(baseJob({ state: '3-progress' }));
    TestBed.inject(TaskPipelinePollService).pipeline.set(agentPipeline());
    TestBed.inject(RunTimelinePollService).timeline.set(runTimeline(1, [runRecord()]));
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    expect(c.agentRunCount()).toBe(1);
    expect(c.agentRunCountLabel()).toBe('1 run');
    expect(c.agentRunTooltip()!.title).toBe('Agent execution · 1 run');
  });

  it('agent-execution row: no runs keeps the dash state — no badge, no tooltip', async () => {
    const fixture = await build(baseJob({ state: '2-ready' }));
    TestBed.inject(TaskPipelinePollService).pipeline.set(agentPipeline());
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const c = fixture.componentInstance;
    expect(c.agentRunCount()).toBe(0);
    expect(c.agentRunTooltip()).toBeNull();
    expect(
      fixture.nativeElement.querySelector('[data-testid="overview-pipeline-agent-runs"]'),
    ).toBeNull();
  });

  it('agent-execution row: the badge is bound to the core row only (aspect rows never carry it)', async () => {
    const fixture = await build(baseJob({ state: '3-progress' }));
    TestBed.inject(TaskPipelinePollService).pipeline.set(agentPipeline());
    TestBed.inject(RunTimelinePollService).timeline.set(runTimeline(7, [runRecord()]));
    try { fixture.detectChanges(); } catch { /* ignore */ }

    const badges = fixture.nativeElement.querySelectorAll('[data-testid="overview-pipeline-agent-runs"]');
    // Exactly one core row exists, so exactly one badge — never on the aspect row.
    expect(badges.length).toBe(1);
    const coreRow = badges[0].closest('[data-testid="overview-pipeline-step"]');
    expect(coreRow?.getAttribute('data-step-id')).toBe('core-agent-run');
  });

  it('session row was removed (component no longer exposes session-id helpers)', async () => {
    const fixture = await build(baseJob({ sessionName: 'c705779a-aaaa-bbbb-cccc-ddddeeeeffff' }));
    // The overview no longer surfaces session id in any row. The session
    // chain remains visible on the protocol pane's session badge. The
    // shortSessionId / copyToClipboard helpers were dropped from the
    // controller; assert they are gone so a future re-add lights up here.
    const proto = OverviewPaneComponent.prototype as unknown as Record<string, unknown>;
    expect(typeof proto['shortSessionId']).toBe('undefined');
    expect(typeof proto['copyToClipboard']).toBe('undefined');
    expect(fixture.componentInstance).toBeTruthy();
  });
});
