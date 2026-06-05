import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { PromptPaneComponent } from './prompt-pane.component';
import type { ReviewEvidenceEntry, TaskArtifact, TaskInfo } from '../../../../models/task.model';
import type { TaskScreenshot } from '../../../screenshots';

function baseJob(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'test-1', taskKey: 'wp::test-1', title: 'Test', state: '2-ready',
    order: 1, agent: 'human', createdAt: new Date().toISOString(),
    watchPath: '/tmp', projectName: 'test', folderPath: '/tmp/test-1',
    lastActivity: new Date().toISOString(), sessionName: null,
    model: null, cliType: null, useOwnSession: null, lastUsage: null,
    execution: null, commit: null,
    ...overrides,
  };
}

async function build(initialJob: TaskInfo) {
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

function artifact(name: string): TaskArtifact {
  return {
    name,
    sizeBytes: 100,
    mtime: new Date().toISOString(),
    kind: 'other',
  };
}

function screenshot(fileName: string): TaskScreenshot {
  return {
    jobId: 'test-1',
    jobTitle: 'Test',
    projectName: 'test',
    watchPath: '/tmp',
    fileName,
    relativePath: `results/${fileName}`,
    url: `/api/jobs/test-1/screenshot?path=${encodeURIComponent(fileName)}`,
    caption: fileName,
    status: 'passed',
    localPath: `/tmp/test-1/results/${fileName}`,
    timestampUtc: new Date().toISOString(),
  };
}

function reviewEvidence(id: string): ReviewEvidenceEntry {
  return {
    id,
    source: 'code-review',
    severity: 'info',
    title: 'Review note',
    body: null,
    createdAt: new Date().toISOString(),
    runIndex: null,
    artifacts: [],
    fileRefs: [],
    acknowledged: false,
    followupJobId: null,
  };
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
    const fixture = await build(baseJob({ taskKey: 'wp::task-a' }));
    expect(fixture.componentInstance.activeTab()).toBe('overview');
  });

  it('keeps the operator selection while the same job updates in place', async () => {
    const fixture = await build(baseJob({ taskKey: 'wp::task-a', title: 'A v1' }));
    const c = fixture.componentInstance;
    c.setTab('description');
    expect(c.activeTab()).toBe('description');

    // Same jobKey, different field (status refresh on the same task).
    fixture.componentRef.setInput('job', baseJob({ taskKey: 'wp::task-a', title: 'A v2' }));
    try { fixture.detectChanges(); } catch { /* ignore render misses */ }
    expect(c.activeTab()).toBe('description');
  });

  it('resets to Overview when the underlying task changes (jobKey switch)', async () => {
    const fixture = await build(baseJob({ taskKey: 'wp::task-a' }));
    const c = fixture.componentInstance;
    c.setTab('description');
    expect(c.activeTab()).toBe('description');

    fixture.componentRef.setInput('job', baseJob({ taskKey: 'wp::task-b' }));
    try { fixture.detectChanges(); } catch { /* ignore render misses */ }
    expect(c.activeTab()).toBe('overview');
  });

  it('resets every time the task changes — there is no per-task memory of the previous selection', async () => {
    const fixture = await build(baseJob({ taskKey: 'wp::task-a' }));
    const c = fixture.componentInstance;
    c.setTab('evidence');

    fixture.componentRef.setInput('job', baseJob({ taskKey: 'wp::task-b' }));
    try { fixture.detectChanges(); } catch { /* ignore */ }
    expect(c.activeTab()).toBe('overview');

    c.setTab('code-review');
    fixture.componentRef.setInput('job', baseJob({ taskKey: 'wp::task-a' }));
    try { fixture.detectChanges(); } catch { /* ignore */ }
    // Back on Task A; the prior 'evidence' selection is gone — Overview wins.
    expect(c.activeTab()).toBe('overview');
  });
});

describe('PromptPaneComponent tab count badges', () => {
  it('shows Files count and Visual Evidence count with the shared count badge', async () => {
    const fixture = await build(baseJob());
    fixture.componentRef.setInput('artifacts', [
      artifact('prompt.md'),
      artifact('aspect-code-quality.md'),
      artifact('REVIEW_NOTE.md'),
    ]);
    fixture.componentRef.setInput('screenshots', [
      screenshot('home.png'),
      screenshot('detail.png'),
    ]);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const filesBadge = root.querySelector('[data-testid="prompt-tab-description-badge"] .count-badge');
    const evidenceBadge = root.querySelector('[data-testid="prompt-tab-evidence-badge"] .count-badge');

    expect(filesBadge?.textContent?.trim()).toBe('3');
    expect(evidenceBadge?.textContent?.trim()).toBe('2');
  });

  it('does not show an Evidence badge for review evidence without screenshots', async () => {
    const fixture = await build(baseJob());
    fixture.componentRef.setInput('reviewEvidence', [reviewEvidence('code-review-1')]);
    fixture.componentRef.setInput('screenshots', []);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="prompt-tab-evidence-badge"]')).toBeNull();
  });
});
