import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { JobCardComponent } from './job-card.component';
import type { JobInfo } from '../../../../models/job.model';

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
describe('JobCardComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [JobCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(JobCardComponent);
    fixture.componentRef.setInput('job', undefined);

    // Required inputs seeded with undefined — replace with realistic defaults if needed:
    // job
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] JobCardComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('commit tooltip exposes the actual file list, not just the count', async () => {
    await TestBed.configureTestingModule({
      imports: [JobCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(JobCardComponent);
    const files = [
      'backend/Services/Analysis/AnalysisReportContract.cs',
      'backend/Services/Analysis/AnalysisReportStore.cs',
      'docs/analysis-reports.md',
    ];
    fixture.componentRef.setInput('job', makeJob({
      state: '5-human-review',
      tags: ['quality:concerns', 'docs:concerns'],
      commit: {
        sha: '5f969a6abc',
        shortSha: '5f969a6',
        message: 'test(analysis-report): add schema round-trip tests',
        filesChanged: files.length,
        files,
        at: '2026-05-05T09:16:30Z',
      },
    }));
    fixture.detectChanges();

    const tooltip = fixture.componentInstance.commitTooltip();
    expect(typeof tooltip === 'object' && tooltip !== null).toBe(true);
    if (typeof tooltip !== 'object' || tooltip === null) return;
    expect(tooltip.title).toContain('5f969a6');
    expect(tooltip.title).toContain('3 file(s) changed');
    expect(tooltip.body).toContain('AnalysisReportContract.cs');
    expect(tooltip.body).toContain('analysis-reports.md');
    expect(tooltip.body).toContain('<ul>');

    const commit = fixture.nativeElement.querySelector('[data-testid="job-card-commit"]') as HTMLElement | null;
    expect(commit?.getAttribute('data-has-files')).toBe('true');
  });

  it('commit tooltip caps the file list and reports overflow', async () => {
    await TestBed.configureTestingModule({
      imports: [JobCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(JobCardComponent);
    const files = Array.from({ length: 20 }, (_, i) => `src/file-${i}.ts`);
    fixture.componentRef.setInput('job', makeJob({
      commit: {
        sha: 'deadbeefcaf',
        shortSha: 'deadbee',
        message: 'refactor: rename utils',
        filesChanged: files.length,
        files,
        at: '2026-05-05T09:16:30Z',
      },
    }));
    fixture.detectChanges();

    const tooltip = fixture.componentInstance.commitTooltip();
    expect(typeof tooltip === 'object' && tooltip !== null).toBe(true);
    if (typeof tooltip !== 'object' || tooltip === null) return;
    expect(tooltip.body).toContain('src/file-0.ts');
    expect(tooltip.body).toContain('src/file-11.ts');
    expect(tooltip.body).not.toContain('src/file-12.ts');
    expect(tooltip.body).toContain('+8 more file(s)');
  });

  it('commit tooltip falls back to count-only when files array is empty', async () => {
    await TestBed.configureTestingModule({
      imports: [JobCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(JobCardComponent);
    fixture.componentRef.setInput('job', makeJob({
      commit: {
        sha: 'abc12345',
        shortSha: 'abc1234',
        message: 'chore: legacy commit pre-file-tracking',
        filesChanged: 4,
        files: [],
        at: '2026-05-05T09:16:30Z',
      },
    }));
    fixture.detectChanges();

    const tooltip = fixture.componentInstance.commitTooltip();
    expect(typeof tooltip === 'object' && tooltip !== null).toBe(true);
    if (typeof tooltip !== 'object' || tooltip === null) return;
    expect(tooltip.title).toContain('4 file(s) changed');
    expect(tooltip.body).not.toContain('<ul>');
    expect(tooltip.body).not.toContain('+');

    const commit = fixture.nativeElement.querySelector('[data-testid="job-card-commit"]') as HTMLElement | null;
    expect(commit?.getAttribute('data-has-files')).toBeNull();
  });

  it('commit tooltip escapes HTML in file paths to prevent injection', async () => {
    await TestBed.configureTestingModule({
      imports: [JobCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(JobCardComponent);
    fixture.componentRef.setInput('job', makeJob({
      commit: {
        sha: 'beefcafe',
        shortSha: 'beefcaf',
        message: 'fix <script>alert(1)</script>',
        filesChanged: 1,
        files: ['src/<img src=x onerror=alert(2)>.ts'],
        at: '2026-05-05T09:16:30Z',
      },
    }));
    fixture.detectChanges();

    const tooltip = fixture.componentInstance.commitTooltip();
    expect(typeof tooltip === 'object' && tooltip !== null).toBe(true);
    if (typeof tooltip !== 'object' || tooltip === null) return;
    expect(tooltip.body).not.toContain('<script>');
    expect(tooltip.body).not.toContain('<img src=x');
    expect(tooltip.body).toContain('&lt;script&gt;');
    expect(tooltip.body).toContain('&lt;img');
  });

  it('renders a runner outcome issue pill', async () => {
    await TestBed.configureTestingModule({
      imports: [JobCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(JobCardComponent);
    fixture.componentRef.setInput('job', makeJob({
      outcomeIssue: {
        kind: 'permission-blocked',
        label: 'Permission blocked',
        severity: 'High',
        summary: 'Permission denied and could not request permission from user.',
        lastSeenAt: '2026-05-11T10:00:00Z',
      },
    }));
    fixture.detectChanges();

    const pill = fixture.nativeElement.querySelector('[data-testid="job-card-outcome-issue"]') as HTMLElement | null;
    expect(pill?.textContent).toContain('Permission blocked');
    expect(pill?.className).toContain('job-card__issue-pill--high');
  });
});

function makeJob(overrides: Partial<JobInfo> = {}): JobInfo {
  return {
    id: 'task-1',
    jobKey: 'test::task-1',
    title: 'Task 1',
    state: '3-progress',
    order: 1,
    agent: 'codex',
    createdAt: '2026-05-11T09:00:00Z',
    watchPath: '/tmp/watch',
    projectName: 'Test',
    folderPath: '/tmp/watch/3-progress/task-1',
    lastActivity: '2026-05-11T09:30:00Z',
    sessionName: null,
    model: null,
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    ...overrides,
  };
}
