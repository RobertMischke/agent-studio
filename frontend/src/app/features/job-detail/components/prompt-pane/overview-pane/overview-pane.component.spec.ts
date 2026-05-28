import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { RunTimelinePollService } from '../../../../polling/services/run-timeline-poll.service';
import { AgentWorkSummaryPollService } from '../../../../polling/services/agent-work-summary-poll.service';
import { OverviewPaneComponent } from './overview-pane.component';
import type { JobInfo } from '../../../../../models/task.model';
import type { AgentWorkSummary } from '../../../../session-events';

function baseJob(overrides: Partial<JobInfo> = {}): JobInfo {
  return {
    id: 'test-1', jobKey: 'wp::test-1', title: 'Test', state: '2-ready',
    order: 1, agent: 'human', createdAt: new Date().toISOString(),
    watchPath: '/tmp', projectName: 'test', folderPath: '/tmp/test-1',
    lastActivity: new Date().toISOString(), sessionName: null,
    model: null, cliType: null, useOwnSession: null, lastUsage: null,
    execution: null, commit: null,
    ...overrides,
  };
}

async function build(job: JobInfo, agentWork: AgentWorkSummary | null = null) {
  await TestBed.configureTestingModule({
    imports: [OverviewPaneComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      RunTimelinePollService,
      AgentWorkSummaryPollService,
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
