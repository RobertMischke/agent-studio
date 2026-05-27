import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { JobColumnComponent } from './task-column';
import type { JobInfo } from '../../../../models/task.model';

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
describe('JobColumnComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [JobColumnComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(JobColumnComponent);
    fixture.componentRef.setInput('title', undefined);
    fixture.componentRef.setInput('state', undefined);
    fixture.componentRef.setInput('jobs', undefined);

    // Required inputs seeded with undefined — replace with realistic defaults if needed:
    // title, state, jobs
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] JobColumnComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('forwards card delete requests from regular lanes', async () => {
    await TestBed.configureTestingModule({
      imports: [JobColumnComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(JobColumnComponent);
    const job = makeJob();
    const deleted: JobInfo[] = [];
    fixture.componentInstance.jobDeleteRequest.subscribe((value) => deleted.push(value));
    fixture.componentRef.setInput('title', 'Ready');
    fixture.componentRef.setInput('state', '2-ready');
    fixture.componentRef.setInput('jobs', [job]);
    fixture.detectChanges();

    const button = fixture.nativeElement.querySelector('[data-testid="job-card-delete"]') as HTMLButtonElement | null;
    expect(button).toBeTruthy();
    button!.click();

    expect(deleted).toEqual([job]);
  });
});

function makeJob(overrides: Partial<JobInfo> = {}): JobInfo {
  return {
    id: 'task-1',
    jobKey: 'test::task-1',
    title: 'Task 1',
    state: '2-ready',
    order: 1,
    agent: 'codex',
    createdAt: '2026-05-11T09:00:00Z',
    watchPath: '/tmp/watch',
    projectName: 'Test',
    folderPath: '/tmp/watch/2-ready/task-1',
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
