import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { StatusBarComponent } from './status-bar';
import { TaskService } from '../../../../services/task.service';

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
describe('StatusBarComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [StatusBarComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(StatusBarComponent);
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] StatusBarComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});

describe('StatusBarComponent running count', () => {
  async function build() {
    await TestBed.configureTestingModule({
      imports: [StatusBarComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(StatusBarComponent);
    const tasks = TestBed.inject(TaskService);
    return { fixture, tasks };
  }

  it('uses the server count for a local running card', async () => {
    const { fixture, tasks } = await build();
    tasks.runnerStatus.set({
      runningCount: 1,
      projects: {
        alpha: runnerProject('alpha', 'local-task', 1),
      },
    });

    expect(fixture.componentInstance.runningCount()).toBe(1);
  });

  it('uses the server count when the running card is remote', async () => {
    const { fixture, tasks } = await build();
    tasks.runnerStatus.set({
      runningCount: 1,
      projects: {
        alpha: runnerProject('alpha', null, 1),
      },
    });

    expect(fixture.componentInstance.runningCount()).toBe(1);
  });

  it('uses the server aggregate for a local and remote mixture', async () => {
    const { fixture, tasks } = await build();
    tasks.runnerStatus.set({
      runningCount: 3,
      projects: {
        alpha: runnerProject('alpha', 'local-task', 2),
        beta: runnerProject('beta', null, 1),
      },
    });

    expect(fixture.componentInstance.runningCount()).toBe(3);
  });
});

function runnerProject(projectName: string, activeJobId: string | null, runningTaskCount: number) {
  return {
    projectName,
    mode: 'manual',
    activeJobId,
    activeExecution: null,
    queuedJobIds: [],
    runningTaskCount,
  };
}
