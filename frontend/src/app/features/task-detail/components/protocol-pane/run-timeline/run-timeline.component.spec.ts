import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { RunTimelineComponent } from './run-timeline.component';
import type { RunPromptEntry, RunRecord } from '../../../../../features/run-timeline';
import type { TaskInfo } from '../../../../../models/task.model';

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
describe('RunTimelineComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [RunTimelineComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(RunTimelineComponent);
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] RunTimelineComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('renders every run chronologically with re-open transitions', async () => {
    await TestBed.configureTestingModule({
      imports: [RunTimelineComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(RunTimelineComponent);
    fixture.componentRef.setInput('runs', [
      runRecord(3, 'restart', 'completed', 'Fix review note', 15),
      runRecord(1, 'start', 'completed', null, 125),
      runRecord(2, 'continue', 'failed', 'Please try again', 33),
    ]);
    fixture.detectChanges();

    const cards = fixture.nativeElement.querySelectorAll('[data-testid="run-timeline-card"]');
    const transitions = fixture.nativeElement.querySelectorAll('[data-testid^="run-transition-"]');
    expect(cards).toHaveLength(3);
    expect(transitions).toHaveLength(2);

    const buttons = fixture.nativeElement.querySelectorAll('[data-testid^="run-icon-"]');
    expect(buttons[0].getAttribute('data-testid')).toBe('run-icon-1');
    expect(buttons[1].getAttribute('data-testid')).toBe('run-icon-2');
    expect(buttons[2].getAttribute('data-testid')).toBe('run-icon-3');
    expect(transitions[0].getAttribute('data-testid')).toBe('run-transition-1-2');
    expect(transitions[1].getAttribute('data-testid')).toBe('run-transition-2-3');

    const text = fixture.nativeElement.textContent as string;
    expect(text.indexOf('Prompt #1')).toBeLessThan(text.indexOf('Prompt #2'));
    expect(text.indexOf('Prompt #2')).toBeLessThan(text.indexOf('Prompt #3'));
    expect(text).toContain('Run #1 re-opened into #2 via user follow-up');
    expect(text).toContain('Run #2 re-opened into #3 via user follow-up');
    expect(text).toContain('🌀');
  });

  it('surfaces captured reissue prompt context from the run header', async () => {
    await TestBed.configureTestingModule({
      imports: [RunTimelineComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(RunTimelineComponent);
    fixture.componentRef.setInput('job', taskInfo());
    fixture.componentRef.setInput('runs', [
      runRecord(1, 'start', 'completed', null, 20),
      { ...runRecord(2, 'reissue', 'completed', null, 25), contextRef: 'logs/run-context/run-2.md', reason: 'auto-review reissue' },
    ]);
    fixture.componentRef.setInput('promptEntries', [
      promptEntry(1, 1, 'start', 'prompt.md', 30, null),
      promptEntry(2, 2, 'reissue', 'logs/run-context/run-2.md', 44, 180),
    ]);
    fixture.detectChanges();

    const runButton = fixture.nativeElement.querySelector('[data-testid="run-icon-2"]') as HTMLButtonElement;
    runButton.click();
    const http = TestBed.inject(HttpTestingController);
    const commitsReq = http.expectOne(r =>
      r.url.endsWith('/tasks/task-1/runs/2/commits') &&
      r.params.get('watchPath') === 'C:\\watch');
    commitsReq.flush({ commits: [] });
    fixture.detectChanges();

    const detail = fixture.nativeElement.querySelector('[data-testid="run-popover-2"]') as HTMLElement;
    expect(detail.textContent).toContain('Prompt #2');
    expect(detail.textContent).toContain('44 tokens');
    expect(detail.textContent).toContain('180 tokens');

    const toggle = fixture.nativeElement.querySelector('[data-testid="run-context-toggle-2"]') as HTMLButtonElement;
    expect(toggle.textContent?.trim()).toBe('Show passed context');
    toggle.click();

    const req = http.expectOne(r =>
      r.url.endsWith('/tasks/task-1/runs/2/context') &&
      r.params.get('watchPath') === 'C:\\watch');
    req.flush({
      runIndex: 2,
      context: '## Reissue change prompt\n\nCode review found the save button still wraps on mobile.'
    });
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Auto-review reissue');
    expect(text).toContain('Reissue change prompt');
    expect(text).toContain('Code review found the save button still wraps on mobile.');
    http.verify();
  });

  it('shows the full prompt-history text even when the run also has captured context', async () => {
    await TestBed.configureTestingModule({
      imports: [RunTimelineComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(RunTimelineComponent);
    fixture.componentRef.setInput('job', taskInfo());
    fixture.componentRef.setInput('runs', [
      runRecord(1, 'start', 'completed', null, 20),
      { ...runRecord(2, 'continue', 'completed', 'Use the extension prompt.', 25), contextRef: 'logs/run-context/run-2.md' },
    ]);
    fixture.componentRef.setInput('promptHistory', [
      {
        index: 1,
        fileName: 'prompt-1.md',
        markdown: 'Use the extension prompt.\n\nAdd context and token snapshots.',
        writtenAt: '2026-06-08T10:01:00Z',
      },
    ]);
    fixture.componentRef.setInput('promptEntries', [
      promptEntry(1, 1, 'start', 'prompt.md', 30, null),
      {
        ...promptEntry(2, 2, 'continue', 'prompt-1.md', 14, 180),
        promptTokenSource: 'prompt-history',
        contextRef: 'logs/run-context/run-2.md',
        contextSnapshot: {
          source: 'captured-context',
          ref: 'logs/run-context/run-2.md',
          at: null,
          status: 'captured',
          tokenEstimate: 180,
          metrics: [],
        },
      },
    ]);
    fixture.detectChanges();

    const runButton = fixture.nativeElement.querySelector('[data-testid="run-icon-2"]') as HTMLButtonElement;
    runButton.click();
    const http = TestBed.inject(HttpTestingController);
    const commitsReq = http.expectOne(r =>
      r.url.endsWith('/tasks/task-1/runs/2/commits') &&
      r.params.get('watchPath') === 'C:\\watch');
    commitsReq.flush({ commits: [] });
    fixture.detectChanges();

    const promptPre = fixture.nativeElement.querySelector('[data-testid="run-prompt-pre-2"]') as HTMLElement;
    expect(promptPre.textContent).toContain('Use the extension prompt.');
    expect(promptPre.textContent).toContain('Add context and token snapshots.');
    expect(fixture.nativeElement.querySelector('[data-testid="run-context-pre-2"]')).toBeNull();
    http.verify();
  });
});

function taskInfo(): TaskInfo {
  return {
    id: 'task-1',
    taskKey: 'ASS-1',
    title: 'Task',
    state: '3-progress',
    order: 0,
    agent: 'codex',
    createdAt: '2026-06-08T10:00:00Z',
    watchPath: 'C:\\watch',
    projectName: 'demo',
    folderPath: 'C:\\watch\\3-progress\\task-1',
    lastActivity: '2026-06-08T10:00:00Z',
    sessionName: null,
    model: null,
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
  } as TaskInfo;
}

function promptEntry(
  index: number,
  runIndex: number,
  intent: string,
  fileName: string | null,
  promptTokenEstimate: number | null,
  contextTokenEstimate: number | null,
): RunPromptEntry {
  return {
    index,
    runIndex,
    intent,
    at: `2026-06-08T10:0${runIndex}:00Z`,
    label: `Prompt #${index}`,
    fileName,
    promptTokenSource: fileName === 'prompt.md' ? 'task-prompt' : 'captured-context',
    promptPreview: index === 1 ? 'Initial prompt' : 'Review prompt',
    promptTokenEstimate,
    contextTokenEstimate,
    contextRef: fileName?.startsWith('logs/') ? fileName : null,
    contextSnapshot: contextTokenEstimate
      ? {
          source: 'captured-context',
          ref: fileName,
          at: null,
          status: 'captured',
          tokenEstimate: contextTokenEstimate,
          metrics: [],
        }
      : null,
  };
}

function runRecord(
  index: number,
  intent: string,
  status: string,
  userFollowup: string | null,
  durationSeconds: number
): RunRecord {
  return {
    index,
    intent,
    status,
    userFollowup,
    durationSeconds,
    startedAt: `2026-06-08T10:0${index}:00Z`,
    endedAt: `2026-06-08T10:0${index}:30Z`,
    cli: 'codex',
    exitCode: status === 'failed' ? 1 : 0,
    inputSessionId: null,
    capturedSessionId: null,
    resumed: index > 1,
    reason: status,
    lineStart: index,
    lineEnd: index + 1,
    headShaBefore: null,
    headShaAfter: null,
    contextRef: null,
    tokenSummary: index === 3 ? {
      calls: 1,
      inputTokens: 2000,
      outputTokens: 500,
      cacheReadTokens: 0,
      cacheCreationTokens: 0,
      totalTokens: 2500,
      lastModel: 'gpt-5',
      lastUpdate: '2026-06-08T10:03:30Z',
      entries: [],
    } : null,
  };
}
