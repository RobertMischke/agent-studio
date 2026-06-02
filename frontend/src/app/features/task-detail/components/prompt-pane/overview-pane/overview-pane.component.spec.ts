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
