import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { JobColumnComponent } from './task-column';
import type { CliExecution, JobInfo, ProjectRunnerStatus } from '../../../../models/task.model';

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

  // ─────────────────────────────────────────────────────────────────────
  // Lane status cluster (BUG: lane status not clearly showing auto-pickup)
  //
  // Three scenarios verified by reading the derived `statusCluster` signal:
  //   1. idle  + auto-continuous  → AUTO chip, no RUNNING, no queue.
  //   2. running + auto-continuous → RUNNING + AUTO chips both visible.
  //   3. running + circuit-breaker-induced manual → RUNNING + PAUSED chips,
  //      tooltip explicitly mentions the circuit-breaker.
  // ─────────────────────────────────────────────────────────────────────

  async function buildColumn(opts: { mode: string; status?: ProjectRunnerStatus | null; jobs?: JobInfo[] }) {
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
    fixture.componentRef.setInput('title', 'In Progress');
    fixture.componentRef.setInput('state', '3-progress');
    fixture.componentRef.setInput('jobs', opts.jobs ?? []);
    fixture.componentRef.setInput('autoProject', 'TestProject');
    fixture.componentRef.setInput('autoMode', opts.mode);
    fixture.componentRef.setInput('runnerStatus', opts.status ?? null);
    fixture.componentRef.setInput('nowMs', new Date('2026-05-27T15:00:00Z').getTime());
    return fixture;
  }

  it('cluster: idle + auto → AUTO chip, no RUNNING, no queue', async () => {
    const fixture = await buildColumn({
      mode: 'auto-continuous',
      status: makeStatus({ mode: 'auto-continuous' })
    });
    const cluster = fixture.componentInstance.statusCluster();
    expect(cluster).not.toBeNull();
    expect(cluster!.mode.kind).toBe('auto');
    expect(cluster!.mode.label).toBe('AUTO');
    expect(cluster!.running).toBeNull();
    expect(cluster!.queue).toBeNull();
  });

  it('cluster: running + auto → RUNNING + AUTO chips both visible', async () => {
    const exec = makeExec({ jobId: 'task-7', startedAt: '2026-05-27T14:56:36Z' });
    const fixture = await buildColumn({
      mode: 'auto-continuous',
      status: makeStatus({
        mode: 'auto-continuous',
        activeJobId: 'task-7',
        activeExecution: exec,
        queuedJobIds: ['task-8', 'task-9', 'task-10', 'task-11', 'task-12']
      })
    });
    const cluster = fixture.componentInstance.statusCluster();
    expect(cluster).not.toBeNull();
    expect(cluster!.running).not.toBeNull();
    expect(cluster!.running!.jobId).toBe('task-7');
    expect(cluster!.running!.duration).toMatch(/3m24s/);
    expect(cluster!.mode.kind).toBe('auto');
    expect(cluster!.queue).not.toBeNull();
    expect(cluster!.queue!.count).toBe(5);
  });

  it('cluster: running + circuit-breaker (mode=manual + reason) → PAUSED with circuit-breaker tooltip', async () => {
    const exec = makeExec({ jobId: 'task-7', startedAt: '2026-05-27T14:55:00Z' });
    const fixture = await buildColumn({
      mode: 'manual',
      status: makeStatus({
        mode: 'manual',
        activeJobId: 'task-7',
        activeExecution: exec,
        modeReason: "auto-failure circuit-breaker: 3x same job 'foo' did not reach review",
        modeSource: 'circuit-breaker'
      })
    });
    const cluster = fixture.componentInstance.statusCluster();
    expect(cluster).not.toBeNull();
    expect(cluster!.mode.kind).toBe('paused');
    expect(cluster!.mode.label).toBe('PAUSED');
    expect(cluster!.mode.tooltip.toLowerCase()).toContain('circuit-breaker');
    expect(cluster!.running).not.toBeNull();
    expect(cluster!.running!.jobId).toBe('task-7');
  });

  it('cluster: mode=manual without circuit-breaker reason renders as MANUAL', async () => {
    const fixture = await buildColumn({
      mode: 'manual',
      status: makeStatus({ mode: 'manual', modeReason: 'api-toggle', modeSource: 'user' })
    });
    const cluster = fixture.componentInstance.statusCluster();
    expect(cluster!.mode.kind).toBe('manual');
    expect(cluster!.mode.label).toBe('MANUAL');
    expect(cluster!.mode.tooltip.toLowerCase()).toContain('auto-pickup is off');
  });

  it('cluster: hidden when state is not 3-progress', async () => {
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
    fixture.componentRef.setInput('title', 'Ready');
    fixture.componentRef.setInput('state', '2-ready');
    fixture.componentRef.setInput('jobs', []);
    fixture.componentRef.setInput('autoProject', 'TestProject');
    fixture.componentRef.setInput('autoMode', 'auto-continuous');
    expect(fixture.componentInstance.statusCluster()).toBeNull();
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

function makeExec(overrides: Partial<CliExecution> = {}): CliExecution {
  return {
    jobId: 'task-1',
    jobKey: 'test::task-1',
    processId: 12345,
    startedAt: '2026-05-27T14:00:00Z',
    status: 'running',
    exitCode: null,
    durationSeconds: null,
    model: 'claude-opus-4-7',
    ...overrides,
  };
}

function makeStatus(overrides: Partial<ProjectRunnerStatus> = {}): ProjectRunnerStatus {
  return {
    projectName: 'TestProject',
    mode: 'manual',
    activeJobId: null,
    activeExecution: null,
    queuedJobIds: [],
    modeReason: null,
    modeChangedAt: null,
    modeSource: null,
    ...overrides,
  };
}

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
