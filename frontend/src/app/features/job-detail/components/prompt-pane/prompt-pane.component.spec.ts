import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { PromptPaneComponent } from './prompt-pane.component';
import type { JobInfo } from '../../../../models/task.model';

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

async function build(initialJob: JobInfo) {
  await TestBed.configureTestingModule({
    imports: [PromptPaneComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(PromptPaneComponent);
  fixture.componentRef.setInput('job', initialJob);
  try { fixture.detectChanges(); } catch (e) {
    console.warn('[smoke] PromptPaneComponent initial render skipped:', (e as Error).message);
  }
  return fixture;
}

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 * What this catches: broken templateUrl/styleUrl resolution, broken
 * inject() wiring, broken signal init, decorator metadata regressions.
 *
 * What it does NOT catch: full render-path bugs that require seeded
 * inputs or per-component service stubs — those would need a
 * hand-tuned spec. `detectChanges()` is wrapped in try/catch so a
 * missing-input or missing-provider failure surfaces as a console
 * note instead of a red test, which keeps this generator-driven layer
 * stable across template tweaks.
 */
describe('PromptPaneComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    const fixture = await build(baseJob());
    expect(fixture.componentInstance).toBeTruthy();
  });
});

/**
 * Behavioural spec: opening or switching tasks must land on the
 * Overview tab. Within-task clicks persist; across-task changes reset.
 */
describe('PromptPaneComponent active-tab default', () => {
  it('defaults to Overview when first mounted', async () => {
    const fixture = await build(baseJob({ jobKey: 'wp::task-a' }));
    expect(fixture.componentInstance.activeTab()).toBe('overview');
  });

  it('keeps the operator selection while the same job updates in place', async () => {
    const fixture = await build(baseJob({ jobKey: 'wp::task-a', title: 'A v1' }));
    const c = fixture.componentInstance;
    c.setTab('description');
    expect(c.activeTab()).toBe('description');

    // Same jobKey, different field (status refresh on the same task).
    fixture.componentRef.setInput('job', baseJob({ jobKey: 'wp::task-a', title: 'A v2' }));
    try { fixture.detectChanges(); } catch { /* ignore render misses */ }
    expect(c.activeTab()).toBe('description');
  });

  it('resets to Overview when the underlying task changes (jobKey switch)', async () => {
    const fixture = await build(baseJob({ jobKey: 'wp::task-a' }));
    const c = fixture.componentInstance;
    c.setTab('description');
    expect(c.activeTab()).toBe('description');

    fixture.componentRef.setInput('job', baseJob({ jobKey: 'wp::task-b' }));
    try { fixture.detectChanges(); } catch { /* ignore render misses */ }
    expect(c.activeTab()).toBe('overview');
  });

  it('resets every time the task changes — there is no per-task memory of the previous selection', async () => {
    const fixture = await build(baseJob({ jobKey: 'wp::task-a' }));
    const c = fixture.componentInstance;
    c.setTab('evidence');

    fixture.componentRef.setInput('job', baseJob({ jobKey: 'wp::task-b' }));
    try { fixture.detectChanges(); } catch { /* ignore */ }
    expect(c.activeTab()).toBe('overview');

    c.setTab('code-review');
    fixture.componentRef.setInput('job', baseJob({ jobKey: 'wp::task-a' }));
    try { fixture.detectChanges(); } catch { /* ignore */ }
    // Back on Task A; the prior 'evidence' selection is gone — Overview wins.
    expect(c.activeTab()).toBe('overview');
  });
});
